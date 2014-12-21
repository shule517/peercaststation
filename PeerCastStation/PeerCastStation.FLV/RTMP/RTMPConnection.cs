﻿using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PeerCastStation.Core;

namespace PeerCastStation.FLV.RTMP
{
	public class RTMPConnection
		: IDisposable
	{
		private static Logger logger = new Logger(typeof(RTMPConnection));
		private Stream inputStream;
		private Stream outputStream;
		public RTMPConnection(Stream input_stream, Stream output_stream)
		{
			this.inputStream  = input_stream;
			this.outputStream = output_stream;
		}

		protected Stream InputStream  { get { return inputStream; } }
		protected Stream OutputStream { get { return outputStream; } }
		public string ClientName { get; private set; }

		private bool disposed = false;
		public virtual void Dispose()
		{
			disposed = true;
			this.OnClose();
			this.inputStream.Close();
			this.outputStream.Close();
		}

		protected virtual void OnClose()
		{
		}

		public void Close()
		{
			Dispose();
		}

		private class QueuedMessage
		{
			public static readonly System.Diagnostics.Stopwatch Stopwatch = new System.Diagnostics.Stopwatch();
			static QueuedMessage()
			{
				Stopwatch.Reset();
				Stopwatch.Start();
			}

			public int ChunkStreamId { get; private set; }
			public RTMPMessage Message { get; private set; }
			public TimeSpan TimeStamp { get; private set; }
			public QueuedMessage(int chunk_stream_id, RTMPMessage msg)
			{
				this.ChunkStreamId = chunk_stream_id;
				this.Message = msg;
				this.TimeStamp = Stopwatch.Elapsed;
			}
		}
		private MessageQueue<QueuedMessage> postMessageQueue = new MessageQueue<QueuedMessage>();

		protected void PostMessage(int chunk_stream_id, RTMPMessage msg)
		{
			postMessageQueue.Enqueue(new QueuedMessage(chunk_stream_id, msg));
		}

		public async Task Run(CancellationToken cancel_token)
		{
			try {
				cancel_token.ThrowIfCancellationRequested();
				await Handshake(cancel_token);
				var local_cancel = new CancellationTokenSource();
				cancel_token.Register(() => local_cancel.Cancel());
				var send_messages = SendServerMessages(local_cancel.Token);
				var recv_messages = RecvAndProcessMessages(local_cancel.Token);
				Task.WaitAny(send_messages, recv_messages);
				local_cancel.Cancel();
				Task.WaitAll(send_messages, recv_messages);
			}
			catch (IOException e) {
				if (!disposed) {
					logger.Error(e);
				}
			}
			catch (AggregateException e) {
				if (!disposed) {
					logger.Error(e);
				}
			}
			finally {
				Close();
			}
		}

		private async Task SendServerMessages(CancellationToken cancel_token)
		{
			while (!cancel_token.IsCancellationRequested) {
				var msg = await postMessageQueue.DequeueAsync(cancel_token);
				await SendMessage(msg.ChunkStreamId, msg.Message, cancel_token);
			}
		}

		protected async Task RecvAndProcessMessages(CancellationToken cancel_token)
		{
			var messages = new Queue<RTMPMessage>();
			while (!cancel_token.IsCancellationRequested) {
				await RecvMessage(messages, cancel_token);
				await ProcessMessages(messages, cancel_token);
				messages.Clear();
			}
		}

		System.Diagnostics.Stopwatch timestampTimer = new System.Diagnostics.Stopwatch();
		private async Task Handshake(CancellationToken cancel_token)
		{
			var rand = new Random();
			await Recv(1, reader => {
				var c0 = reader.ReadByte();
				if (c0!=3) {
					throw new InvalidDataException();
				}
			}, cancel_token);
			await Send(new byte[] { 0x03 }, cancel_token);
			var s1vec = new byte[1528];
			rand.NextBytes(s1vec);
			await Send(writer => {
				writer.Write(0);
				writer.Write(0);
				writer.Write(s1vec);
			}, cancel_token);
			await Recv(1536, async reader => {
				var c1time = reader.ReadInt32();
				var c1zero = reader.ReadInt32();
				var c1vec  = reader.ReadBytes(1528);
				await Send(writer => {
					writer.Write(c1time);
					writer.Write(c1zero);
					writer.Write(c1vec);
				}, cancel_token);
			}, cancel_token);
			await Recv(1536, reader => {
				var c2time = reader.ReadInt32();
				var c2zero = reader.ReadInt32();
				var c2vec = reader.ReadBytes(1528);
				if (!s1vec.SequenceEqual(c2vec)) {
					throw new InvalidDataException("C2 random vector is not matched.");
				}
			}, cancel_token);
			timestampTimer.Reset();
			timestampTimer.Start();
			logger.Debug("Handshake completed");
		}

		private async Task Recv(byte[] buf, int offset, int len, CancellationToken cancel_token)
		{
			var pos = 0;
			while (pos<len) {
				var recvd = await InputStream.ReadAsync(buf, offset+pos, len-pos, cancel_token);
				if (recvd==0) throw new EndOfStreamException();
				pos += recvd;
			}
		}

		private async Task<RTMPBinaryReader> Recv(int len, CancellationToken cancel_token)
		{
			var buf = new byte[len];
			await Recv(buf, 0, len, cancel_token);
			return new RTMPBinaryReader(new MemoryStream(buf));
		}

		private async Task Recv(int len, Action<RTMPBinaryReader> reader_action, CancellationToken cancel_token)
		{
			reader_action.Invoke(await Recv(len, cancel_token));
		}

		private async Task Recv(int len, Func<RTMPBinaryReader, Task> reader_action, CancellationToken cancel_token)
		{
			await reader_action.Invoke(await Recv(len, cancel_token));
		}

		private Task Send(byte[] data, CancellationToken cancel_token)
		{
			return Task.Run(() => {
				lock (OutputStream) {
					OutputStream.WriteAsync(data, 0, data.Length, cancel_token).Wait();
				}
			});
		}

		private Task Send(Action<RTMPBinaryWriter> write_action, CancellationToken cancel_token)
		{
			var buf = new MemoryStream();
			using (var writer=new RTMPBinaryWriter(buf)) {
				write_action.Invoke(writer);
			}
			buf.Close();
			var ary = buf.ToArray();
			return Task.Run(() => {
				lock (OutputStream) {
					OutputStream.WriteAsync(ary, 0, ary.Length, cancel_token).Wait();
				}
			});
		}

		private async Task Send(Func<RTMPBinaryWriter, Task> write_action, CancellationToken cancel_token)
		{
			var buf = new MemoryStream();
			using (var writer=new RTMPBinaryWriter(buf)) {
				await write_action.Invoke(writer);
			}
			buf.Close();
			var ary = buf.ToArray();
			await Task.Run(() => {
				lock (OutputStream) {
					OutputStream.WriteAsync(ary, 0, ary.Length, cancel_token).Wait();
				}
			});
		}

		protected long Now {
			get { return timestampTimer.ElapsedMilliseconds; }
		}

		protected int ObjectEncoding {
			get { return objectEncoding; }
		}

		private int nextClientId    = 1;
		private int nextStreamId    = 1;
		private int objectEncoding  = 0;
		private int sendChunkSize   = 128;
		private int recvChunkSize   = 128;
		private long sendWindowSize = 0x7FFFFFFF;
		private PeerBandwidthLimitType sendWindowLimitType = PeerBandwidthLimitType.Hard;
		private long recvWindowSize = 0x7FFFFFFF;
		private long receivedSize   = 0;
		private long sequenceNumber = 0;
		protected async Task<int> RecvStream(byte[] buf, int offset, int len, CancellationToken cancel_token)
		{
			if (len+receivedSize>=recvWindowSize) {
				var len1 = (int)(recvWindowSize-receivedSize);
				await Recv(buf, offset, len1, cancel_token);
				receivedSize   += len1;
				sequenceNumber += len1;
				await SendMessage(2, new AckMessage(this.Now, 0, sequenceNumber), cancel_token);
				var len2 = len - len1;
				await Recv(buf, offset+len1, len2, cancel_token);
				receivedSize    = len2; //reset
				sequenceNumber += len2;
			}
			else {
				await Recv(buf, offset, len, cancel_token);
				receivedSize   += len;
				sequenceNumber += len;
			}
			return len;
		}

		protected async Task<byte> RecvStreamByte(CancellationToken cancel_token)
		{
			var buf = new byte[1];
			await RecvStream(buf, 0, 1, cancel_token);
			return buf[0];
		}

		protected async Task RecvStream(int len, Action<RTMPBinaryReader> reader_action, CancellationToken cancel_token)
		{
			var buf = new byte[len];
			await RecvStream(buf, 0, len, cancel_token);
			using (var reader=new RTMPBinaryReader(new MemoryStream(buf))) {
				reader_action.Invoke(reader);
			}
		}

		protected async Task RecvStream(int len, Func<RTMPBinaryReader, Task> reader_action, CancellationToken cancel_token)
		{
			var buf = new byte[len];
			await RecvStream(buf, 0, len, cancel_token);
			using (var reader=new RTMPBinaryReader(new MemoryStream(buf))) {
				await reader_action.Invoke(reader);
			}
		}

		protected class RTMPMessageBuilder
		{
			public long Timestamp            { get; set; }
			public long TimestampDelta       { get; set; }
			public int  TypeId               { get; set; }
			public long ChunkMessageStreamId { get; set; }
			public int  BodyLength           { get; set; }
			public int  ReceivedLength       { get; set; }
			public byte[] Body               { get; set; }

			public RTMPMessageBuilder(
				RTMPMessageBuilder x,
				long timestamp,
				int  type_id,
				long chunk_message_stream_id,
				int  body_length)
			{
				Timestamp            = timestamp;
				TimestampDelta       = x!=null ? timestamp-x.Timestamp : 0;
				TypeId               = type_id;
				ChunkMessageStreamId = chunk_message_stream_id;
				BodyLength           = body_length;
				ReceivedLength       = 0;
				Body                 = new byte[BodyLength];
			}

			public RTMPMessageBuilder(
				RTMPMessageBuilder x,
				long timestamp_delta,
				int  type_id,
				int  body_length)
			{
				Timestamp            = x.Timestamp + timestamp_delta;
				TimestampDelta       = timestamp_delta;
				TypeId               = type_id;
				ChunkMessageStreamId = x.ChunkMessageStreamId;
				BodyLength           = body_length;
				ReceivedLength       = 0;
				Body                 = new byte[BodyLength];
			}

			public RTMPMessageBuilder(
				RTMPMessageBuilder x,
				long timestamp_delta)
			{
				Timestamp            = x.Timestamp + timestamp_delta;
				TimestampDelta       = timestamp_delta;
				TypeId               = x.TypeId;
				ChunkMessageStreamId = x.ChunkMessageStreamId;
				BodyLength           = x.BodyLength;
				ReceivedLength       = 0;
				Body                 = new byte[BodyLength];
			}

			public RTMPMessageBuilder(RTMPMessageBuilder x)
			{
				Timestamp            = x.Timestamp + x.TimestampDelta;
				TypeId               = x.TypeId;
				ChunkMessageStreamId = x.ChunkMessageStreamId;
				BodyLength           = x.BodyLength;
				ReceivedLength       = 0;
				Body                 = new byte[BodyLength];
			}

			public RTMPMessage ToMessage()
			{
				return new RTMPMessage((RTMPMessageType)TypeId, Timestamp, ChunkMessageStreamId, Body);
			}

			public void Abort()
			{
				this.ReceivedLength = this.BodyLength;
			}
		}

		private Dictionary<int, RTMPMessageBuilder> lastMessages = new Dictionary<int,RTMPMessageBuilder>();
		private async Task RecvMessage(Queue<RTMPMessage> messages, CancellationToken cancel_token)
		{
			var basic_header = await RecvStreamByte(cancel_token);
			var chunk_stream_id = basic_header & 0x3F;
			if (chunk_stream_id==0) {
				chunk_stream_id = await RecvStreamByte(cancel_token) + 64;
			}
			else if (chunk_stream_id==1) {
				await RecvStream(2, reader => {
					var b0 = reader.ReadByte();
					var b1 = reader.ReadByte();
					chunk_stream_id = (b1*256 | b0) + 64;
				}, cancel_token);
			}

			RTMPMessageBuilder msg = null;
			RTMPMessageBuilder last_msg = null;
			lastMessages.TryGetValue(chunk_stream_id, out last_msg);
			switch ((basic_header & 0xC0)>>6) {
			case 0:
				await RecvStream(11, async reader => {
					long timestamp  = reader.ReadUInt24();
					var body_length = reader.ReadUInt24();
					var type_id     = reader.ReadByte();
					var stream_id   = reader.ReadUInt32LE();
					if (timestamp==0xFFFFFF) {
						await RecvStream(4, ext_reader => {
							timestamp = ext_reader.ReadUInt32();
						}, cancel_token);
					}
					msg = new RTMPMessageBuilder(
						last_msg,
						timestamp,
						type_id,
						stream_id,
						body_length);
					lastMessages[chunk_stream_id] = msg;
				}, cancel_token);
				break;
			case 1:
				await RecvStream(7, async reader => {
					long timestamp_delta = reader.ReadUInt24();
					var body_length      = reader.ReadUInt24();
					var type_id          = reader.ReadByte();
					if (timestamp_delta==0xFFFFFF) {
						await RecvStream(4, ext_reader => {
							timestamp_delta = ext_reader.ReadUInt32();
						}, cancel_token);
					}
					msg = new RTMPMessageBuilder(
						last_msg,
						timestamp_delta,
						type_id,
						body_length);
					lastMessages[chunk_stream_id] = msg;
				}, cancel_token);
				break;
			case 2:
				await RecvStream(3, async reader => {
					long timestamp_delta = reader.ReadUInt24();
					if (timestamp_delta==0xFFFFFF) {
						await Recv(4, ext_reader => {
							timestamp_delta = ext_reader.ReadUInt32();
						}, cancel_token);
					}
					msg = new RTMPMessageBuilder(last_msg, timestamp_delta);
					lastMessages[chunk_stream_id] = msg;
				}, cancel_token);
				break;
			case 3:
				msg = last_msg;
				if (msg.ReceivedLength>=msg.BodyLength) {
					msg = new RTMPMessageBuilder(last_msg);
					lastMessages[chunk_stream_id] = msg;
				}
				break;
			}

			msg.ReceivedLength += await RecvStream(
				msg.Body,
				msg.ReceivedLength,
				Math.Min(recvChunkSize, msg.BodyLength-msg.ReceivedLength),
				cancel_token);
			if (msg.ReceivedLength>=msg.BodyLength) {
				messages.Enqueue(msg.ToMessage());
			}
		}

		protected async Task SendMessage(int chunk_stream_id, RTMPMessage msg, CancellationToken cancel_token)
		{
			int offset = 0;
			int fmt = 0;
			while (msg.Body.Length-offset>0) {
				switch (fmt) {
				case 0:
					await Send(writer => {
						writer.Write((byte)((fmt<<6) | chunk_stream_id));
						if (msg.Timestamp>0xFFFFFF) {
							writer.WriteUInt24(0xFFFFFF);
						}
						else {
							writer.WriteUInt24((int)msg.Timestamp);
						}
						writer.WriteUInt24(msg.Body.Length);
						writer.Write((byte)msg.MessageType);
						writer.WriteUInt32LE(msg.StreamId);
						if (msg.Timestamp>0xFFFFFF) {
							writer.WriteUInt32(msg.Timestamp);
						}
						int chunk_len = Math.Min(sendChunkSize, msg.Body.Length-offset);
						writer.Write(msg.Body, offset, chunk_len);
						offset += chunk_len;
					}, cancel_token);
					fmt = 3;
					break;
				case 3:
					await Send(writer => {
						writer.Write((byte)((fmt<<6) | chunk_stream_id));
						int chunk_len = Math.Min(sendChunkSize, msg.Body.Length-offset);
						writer.Write(msg.Body, offset, chunk_len);
						offset += chunk_len;
					}, cancel_token);
					break;
				}
			}
		}

		private async Task ProcessMessages(IEnumerable<RTMPMessage> messages, CancellationToken cancel_token)
		{
			foreach (var msg in messages) {
				await ProcessMessage(msg, cancel_token);
			}
			FlushBuffer();
		}

		protected virtual async Task ProcessMessage(RTMPMessage msg, CancellationToken cancel_token)
		{
			switch (msg.MessageType) {
			case RTMPMessageType.SetChunkSize:
				await OnSetChunkSize(new SetChunkSizeMessage(msg), cancel_token);
				break;
			case RTMPMessageType.Abort:
				await OnAbort(new AbortMessage(msg), cancel_token);
				break;
			case RTMPMessageType.Ack:
				//Do nothing
				break;
			case RTMPMessageType.UserControl:
				await OnUserControl(new UserControlMessage(msg), cancel_token);
				break;
			case RTMPMessageType.SetWindowSize:
				await OnSetWindowSize(new SetWindowSizeMessage(msg), cancel_token);
				break;
			case RTMPMessageType.SetPeerBandwidth:
				await OnSetPeerBandwidth(new SetPeerBandwidthMessage(msg), cancel_token);
				break;
			case RTMPMessageType.Audio:
				await OnAudio(msg, cancel_token);
				break;
			case RTMPMessageType.Video:
				await OnVideo(msg, cancel_token);
				break;
			case RTMPMessageType.DataAMF3:
				await OnData(new DataAMF3Message(msg), cancel_token);
				break;
			case RTMPMessageType.DataAMF0:
				await OnData(new DataAMF0Message(msg), cancel_token);
				break;
			case RTMPMessageType.CommandAMF3:
				await OnCommand(new CommandAMF3Message(msg), cancel_token);
				break;
			case RTMPMessageType.CommandAMF0:
				await OnCommand(new CommandAMF0Message(msg), cancel_token);
				break;
			case RTMPMessageType.Aggregate:
				await OnAggregate(new AggregateMessage(msg), cancel_token);
				break;
			case RTMPMessageType.SharedObjectAMF3:
			case RTMPMessageType.SharedObjectAMF0:
				//TODO:Not implemented
				break;
			default:
				//TODO:Unknown message
				break;
			}
		}

		protected virtual async Task OnSetChunkSize(SetChunkSizeMessage msg, CancellationToken cancel_token)
		{
			recvChunkSize = Math.Min(msg.ChunkSize, 0xFFFFFF);
		}

		protected virtual async Task OnAbort(AbortMessage msg, CancellationToken cancel_token)
		{
			RTMPMessageBuilder builder;
			if (lastMessages.TryGetValue(msg.TargetChunkStream, out builder)) {
				builder.Abort();
			}
		}

		protected virtual async Task OnUserControl(UserControlMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual async Task OnSetWindowSize(SetWindowSizeMessage msg, CancellationToken cancel_token)
		{
			recvWindowSize = msg.WindowSize;
			receivedSize = 0;
	 }

		protected virtual async Task OnSetPeerBandwidth(SetPeerBandwidthMessage msg, CancellationToken cancel_token)
		{
			switch (msg.LimitType) {
			case PeerBandwidthLimitType.Hard:
				sendWindowSize = msg.PeerBandwidth;
				sendWindowLimitType = msg.LimitType;
				break;
			case PeerBandwidthLimitType.Soft:
				sendWindowSize = Math.Min(sendWindowSize, msg.PeerBandwidth);
				sendWindowLimitType = msg.LimitType;
				break;
			case PeerBandwidthLimitType.Dynamic:
				if (sendWindowLimitType==PeerBandwidthLimitType.Hard) {
					sendWindowSize = msg.PeerBandwidth;
					sendWindowLimitType = msg.LimitType;
				}
				break;
			}
		}

		protected virtual async Task OnAudio(RTMPMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual async Task OnVideo(RTMPMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual async Task OnData(DataMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual void FlushBuffer()
		{
		}

		protected virtual async Task OnCommand(CommandMessage msg, CancellationToken cancel_token)
		{
			logger.Debug("Command: {0}", msg.CommandName);
			if (msg.StreamId==0) {
				//NetConnection commands
				switch (msg.CommandName) {
				case "connect":      await OnCommandConnect(msg, cancel_token); break;
				case "call":         await OnCommandCall(msg, cancel_token); break;
				case "close":        await OnCommandClose(msg, cancel_token); break;
				case "createStream": await OnCommandCreateStream(msg, cancel_token); break;
				}
			}
			else {
				//NetStream commands
				switch (msg.CommandName) {
				case "publish":      await OnCommandPublish(msg, cancel_token); break;
				case "play":         await OnCommandPlay(msg, cancel_token); break;
				case "play2":
				case "deleteStream":
				case "closeStream":
				case "receiveAudio":
				case "receiveVideo":
				case "seek":
				case "pause":
				default:
					break;
				}
			}
		}

		protected virtual async Task OnCommandConnect(CommandMessage msg, CancellationToken cancel_token)
		{
			objectEncoding = ((int)msg.CommandObject["objectEncoding"])==3 ? 3 : 0;
			ClientName     = (string)msg.CommandObject["flashVer"];
			logger.Debug("Connect: tcUrl:{0}", (string)msg.CommandObject["tcUrl"]);
			await SendMessage(2, new SetWindowSizeMessage(this.Now, 0, recvWindowSize), cancel_token);
			await SendMessage(2, new SetPeerBandwidthMessage(this.Now, 0, sendWindowSize, PeerBandwidthLimitType.Hard), cancel_token);
			await SendMessage(2, new UserControlMessage.StreamBeginMessage(this.Now, 0, 0), cancel_token);
			var response = CommandMessage.Create(
				objectEncoding,
				this.Now,
				msg.StreamId,
				"_result",
				msg.TransactionId,
				new AMF.AMFValue(new AMF.AMFObject {
					{ "fmsVer",       "FMS/3,5,5,2004" },
					{ "capabilities", 31 },
					{ "mode",         1 },
				}),
				new AMF.AMFValue(new AMF.AMFObject {
					{ "level",          "status" },
					{ "code",           "NetConnection.Connect.Success" },
					{ "description",    "Connection succeeded" },
					{ "data",           new AMF.AMFObject { { "version", "3,5,5,2004" } } },
					{ "clientId",       nextClientId++ },
					{ "objectEncoding", objectEncoding },
				})
			);
			if (msg.TransactionId!=0) {
				await SendMessage(3, response, cancel_token);
			}
		}

		protected virtual async Task OnCommandCall(CommandMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual async Task OnCommandClose(CommandMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual async Task OnCommandCreateStream(CommandMessage msg, CancellationToken cancel_token)
		{
			var new_stream_id = nextStreamId++;
			var response = CommandMessage.Create(
				objectEncoding,
				this.Now,
				msg.StreamId,
				"_result",
				msg.TransactionId,
				null,
				new AMF.AMFValue(new_stream_id)
			);
			if (msg.TransactionId!=0) {
				await SendMessage(3, response, cancel_token);
			}
		}

		protected virtual async Task OnCommandPublish(CommandMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual async Task OnCommandPlay(CommandMessage msg, CancellationToken cancel_token)
		{
		}

		protected virtual async Task OnAggregate(AggregateMessage msg, CancellationToken cancel_token)
		{
			await ProcessMessages(msg.Messages, cancel_token);
		}

	}
}
