﻿module TestCommon

open System
open PeerCastStation.Core
open PeerCastStation.Core.Http
open Owin
open System.Net
open Xunit

let allocateEndPoint localAddr =
    let listener = System.Net.Sockets.TcpListener(localAddr, 0)
    try
        listener.Start()
        listener.LocalEndpoint :?> IPEndPoint
    finally
        listener.Stop()

type DummySourceStream (sstype) =
    let runTask = System.Threading.Tasks.TaskCompletionSource<StopReason>()
    interface ISourceStream with 
        member this.Run () = 
            runTask.Task

        member this.Reconnect () = ()
        member this.Post (from, packet) = ()
        member this.Type = sstype
        member this.Status = SourceStreamStatus.Receiving
        member this.GetConnectionInfo () =
            ConnectionInfo("dummy", ConnectionType.Source, ConnectionStatus.Connected, "", null, RemoteHostStatus.Local, Nullable(), Nullable 0L, Nullable 0.0f, Nullable 0.0f, Nullable(), Nullable(), "dummy")
        member this.Dispose () =
            runTask.TrySetResult(StopReason.UserShutdown) |> ignore

type DummyBroadcastChannel (peercast, network, channelId) =
    inherit Channel(peercast, network, channelId)

    let mutable relayable = true
    member this.Relayable
        with get ()    = relayable
        and  set value = relayable <- value

    override this.IsRelayable(local) =
        if relayable then
            base.IsRelayable(local)
        else
            false

    override this.MakeRelayable(local) =
        if relayable then
            base.MakeRelayable(local)
        else
            false

    override this.IsBroadcasting = true
    override this.CreateSourceStream (source_uri) =
        new DummySourceStream(SourceStreamType.Broadcast)
        :> ISourceStream

let createChannelInfo name contentType =
    let info = AtomCollection()
    info.SetChanInfoName name
    info.SetChanInfoType contentType
    ChannelInfo info

let registerApp path appFunc (owinHost:PeerCastStation.Core.Http.OwinHost) =
    owinHost.Register(
        fun builder ->
            builder.Map(
                string path,
                fun builder ->
                    builder.Run (fun env ->
                        appFunc env
                        |> Async.StartAsTask
                        :> System.Threading.Tasks.Task
                    )
            )
            |> ignore
    ) |> ignore

let registerAppWithType appType path appFunc (owinHost:PeerCastStation.Core.Http.OwinHost) =
    owinHost.Register(
        fun builder ->
            builder.Map(
                string path,
                fun builder ->
                    builder.UseAuth appType |> ignore
                    builder.Run (fun env ->
                        appFunc env
                        |> Async.StartAsTask
                        :> System.Threading.Tasks.Task
                    )
            )
            |> ignore
    ) |> ignore

module Opaque =
    open System.Collections.Generic
    open System.Threading
    open System.Threading.Tasks
    let upgrade (env:IDictionary<string,obj>) handler =
        let upgrade =
            env.["opaque.Upgrade"]
            :?> Action<IDictionary<string,obj>,Func<IDictionary<string,obj>,Task>>
        upgrade.Invoke(
            Dictionary<string,obj>(),
            fun opaqueEnv ->
                let ct =
                    opaqueEnv.["opaque.CallCancelled"]
                    :?> CancellationToken
                Async.StartAsTask(handler opaqueEnv, TaskCreationOptions.None, ct)
                :> Task
        )

    let stream (opaqueEnv:IDictionary<string,obj>) =
        opaqueEnv.["opaque.Stream"]
        :?> System.IO.Stream

type AuthInfo = { id: string; pass: string }
module AuthInfo =
    let toToken info =
        let { id=id; pass=pass } = info
        sprintf "%s:%s" id pass
        |> System.Text.Encoding.ASCII.GetBytes
        |> Convert.ToBase64String

    let toKey info =
        let { id=id; pass=pass } = info
        AuthenticationKey(id, pass)

let pecaWithOwinHostAccessControl acinfo endpoint buildFunc =
    let peca = new PeerCast()
    let owinHost = new PeerCastStation.Core.Http.OwinHost(null, peca)
    buildFunc owinHost
    |> ignore
    peca.OutputStreamFactories.Add(PeerCastStation.Core.Http.OwinHostOutputStreamFactory(peca, owinHost))
    let listener =
        peca.StartListen(
            endpoint,
            OutputStreamType.None,
            OutputStreamType.None
        )
    listener.LoopbackAccessControlInfo <- acinfo
    peca

let pecaWithOwinHost endpoint buildFunc =
    pecaWithOwinHostAccessControl (AccessControlInfo(OutputStreamType.All, false, null)) endpoint buildFunc

module WebRequest =
    let addHeader (header:string) value (req:WebRequest) =
        req.Headers.Add(header, value)
        req

module Assert =
    let ExpectStatusCode code (req:WebRequest) =
        try
            req.GetResponse() |> ignore
            Assert.True(false)
        with
        | :? WebException as ex ->
            Assert.Equal(WebExceptionStatus.ProtocolError, ex.Status)
            Assert.Equal(code, (ex.Response :?> HttpWebResponse).StatusCode)

    let ExpectResponse expected (req:WebRequest) =
        let res = req.GetResponse()
        use strm = new System.IO.StreamReader(res.GetResponseStream())
        Assert.Equal(expected, strm.ReadToEnd())

    let ExpectAtomName expected (atom:Atom) =
        Assert.Equal(expected, atom.Name)
