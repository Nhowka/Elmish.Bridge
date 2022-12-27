namespace Elmish.Bridge

open System
open System.Net.WebSockets
open System.Threading
open Elmish
open Fable.Remoting.Json
open Newtonsoft.Json

//Configures the transport of the custom serializer
type SerializerResult =
    | Text of string
    | Binary of byte []

type BridgeConfig<'Msg,'ElmishMsg> =
    { path : string
      whenDown : 'ElmishMsg option
      mapping :  'Msg -> 'ElmishMsg
      customSerializers: Map<string, obj -> SerializerResult>
      retryTime : int
      name : string option}

[<RequireQualifiedAccess>]
module Bridge =
    let mutable private mappings : Map<string option, Map<string, obj -> SerializerResult> * (string -> unit)> = Map.empty
    let mutable private rpcMappings : Map<Guid, (string -> unit) * Guid> = Map.empty
    let private fableConverter = FableJsonConverter() :> JsonConverter
    let private serialize result = JsonConvert.SerializeObject(result, [| fableConverter |])
    let private settings = JsonSerializerSettings(DateParseHandling = DateParseHandling.None, Converters = [| fableConverter |])
    /// Create a new `BridgeConfig` with the set endpoint
    let endpoint endpoint =
        {
            path = endpoint
            whenDown = None
            mapping = id
            customSerializers = Map.empty
            retryTime = 1
            name = None
        }

    /// Set a message to be sent when connection is lost.
    let withWhenDown msg this =
        { this with whenDown = Some msg }

    /// Set a name for this bridge if you want to have a secondary one.
    let withName name this =
        { this with name = Some name }

    /// Configure how many seconds before reconnecting when the connection is lost.
    /// Values below 1 are ignored
    let withRetryTime sec this =
        if sec < 1 then
            this
        else
            { this with retryTime = sec}

    /// Configure a mapping to the top-level message so the server can send an inner message
    /// That enables using just a subset of the messages on the shared project
    let withMapping map this =
        {
            whenDown = this.whenDown
            path = this.path
            mapping = map
            customSerializers = this.customSerializers
            retryTime = this.retryTime
            name = this.name
        }

    /// Register a custom serializer
    let withCustomSerializer (serializer: 'a -> SerializerResult) (this:BridgeConfig<'Msg,'ElmishMsg>) =
        {
            whenDown = this.whenDown
            path = this.path
            mapping = this.mapping
            customSerializers =
                this.customSerializers
                |> Map.add (typeof<'a>.FullName.Replace("+",".")) (fun e -> serializer (e :?> 'a))
            retryTime = this.retryTime
            name = this.name
        }



    let internal sender(server: 'Server, name: string option) =
        let sentTypeName =
            typeof<'Server>.FullName.Replace('+','.')

        mappings
        |> Map.tryFind name
        |> Option.iter(fun (m,o) ->
            let serializer =
                m
                |> Map.tryFind sentTypeName
                |> Option.defaultValue
                    (serialize >> Text)
            let serialized =
                match serializer server with
                | Text e -> e
                | Binary b -> System.Convert.ToBase64String b
            serialize(sentTypeName, serialized) |> o )

    let internal rpcSender(guid:System.Guid, value: 'value, name: string option) =
        mappings
        |> Map.tryFind name
        |> Option.iter(fun (_,o) ->
            serialize(sprintf "RC|%O" guid, serialize value) |> o )

    let internal rpcAsker(f: IReplyChannel<'T> -> 'Server, bridgeName ) =
        Async.FromContinuations(fun (cont: 'T -> unit, econt: exn -> unit, _) ->
            let guidValue = Guid.NewGuid()
            let guidExn = Guid.NewGuid()
            let sentTypeName = typeof<'Server>.FullName.Replace('+','.')

            let reply (cont : 'a -> unit) s =
                JsonConvert.DeserializeObject<'a>(s, settings)  |> cont

            rpcMappings <-
                rpcMappings
                |> Map.add guidExn ((fun s -> reply econt s), guidValue)
                |> Map.add guidValue ((fun s -> reply cont s), guidExn)

            mappings
            |> Map.tryFind bridgeName
            |> function
               | None -> econt (exn("Bridge does not exist"))
               | Some (_,s) ->
                    let serialized = serialize (f {ValueId = guidValue; ExceptionId = guidExn})
                    s (serialize (sprintf "RS|%s" sentTypeName, serialized))
        )

    let Send (server : 'Server) = sender(server, None)

    let NamedSend (name:string, server : 'Server) = sender(server, Some name)

    let AskServer(f: IReplyChannel<'T> -> 'Server) : Async<'T> =
        rpcAsker(f, None)

    let AskNamedServer(f: IReplyChannel<'T> -> 'Server, name) : Async<'T> =
        rpcAsker(f, Some name)


    let internal attach (config:BridgeConfig<'Msg,'ElmishMsg>) =
        let ws : (ClientWebSocket option * bool)  ref = ref (None, false)
        let dispatcher dispatch =
            let rec websocket server (r:(ClientWebSocket option * bool) ref) =
                lock ws (fun () ->
                    match r.Value with
                    | None, false ->
                        async {
                            let ws = new ClientWebSocket()
                            r.Value <- Some ws, false
                            try
                                do! ws.ConnectAsync(Uri(server), CancellationToken.None) |> Async.AwaitTask
                            with
                            | _ ->
                                (ws :> IDisposable).Dispose()
                                r.Value <- None, false
                                config.whenDown |> Option.iter dispatch}
                        |> Async.StartImmediate
                    | Some _, _ | None, true -> ())
            websocket config.path ws
            let recBuffer = ArraySegment(Array.zeroCreate 4096)
            let cleanSocket (webs : ClientWebSocket) =
                async {
                    do! webs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None)
                                |> Async.AwaitTask |> Async.Catch |> Async.Ignore
                    (webs :> IDisposable).Dispose()
                    ws.Value <- None, false
                    config.whenDown |> Option.iter dispatch
                    do! Async.Sleep (1000 * config.retryTime)
                }
            let rec receiver buffer =
              async {
                    match ws.Value with
                    | Some webs, _ when webs.State = WebSocketState.Open ->
                      try
                        let! msg = webs.ReceiveAsync(recBuffer,CancellationToken.None) |> Async.AwaitTask
                        match msg.MessageType,recBuffer.Array.[0..msg.Count-1],msg.EndOfMessage,msg.CloseStatus with
                        |_,_,_,s when s.HasValue ->
                            do! cleanSocket webs
                            return! receiver []
                        | WebSocketMessageType.Text, data, complete, _ ->
                            let data = data::buffer
                            if complete then
                                let data = data |> List.rev |> Array.concat
                                let message = System.Text.Encoding.UTF8.GetString data
                                if message.StartsWith "R" then
                                    let guid = (System.Guid.Parse message.[1..36])
                                    let json = message.[37..]
                                    rpcMappings
                                    |> Map.tryFind  guid
                                    |> Option.iter(fun (f,og) ->
                                        f json
                                        rpcMappings <-
                                            rpcMappings
                                            |> Map.remove guid
                                            |> Map.remove og)
                                 elif message.StartsWith "E" then
                                    let guid = (System.Guid.Parse message.[1..])
                                    rpcMappings
                                    |> Map.tryFind  guid
                                    |> Option.iter(fun (f,og) ->
                                        f (serialize (exn("Server couldn't process your message")))
                                        rpcMappings <-
                                            rpcMappings
                                            |> Map.remove guid
                                            |> Map.remove og)
                                 else
                                    let parsedJson =
                                        try
                                            JsonConvert.DeserializeObject<'Msg>(message, settings) |> Ok
                                        with
                                        | ex ->
                                            Error ex.Message
                                    match parsedJson with
                                    | Ok msg -> msg |> config.mapping |> dispatch
                                    | Error er -> eprintfn "%s" er
                                return! receiver []
                            else
                                return! receiver data
                        | _ -> return! receiver buffer
                      with
                      | _ ->
                            do! cleanSocket webs
                            return! receiver []
                    | Some webs, _ when webs.State = WebSocketState.Connecting ->
                        do! Async.Sleep (1000 * config.retryTime)
                        return! receiver []
                    | Some webs, _ ->
                        do! cleanSocket webs
                        return! receiver []
                    | None, _ ->
                        websocket config.path ws
                        return! receiver []
                }
            Async.StartImmediate (receiver [])
            let sender = MailboxProcessor.Start (fun mb ->
                let rec loop () = async {
                    let! (msg : string) = mb.Receive()
                    match ws.Value with
                    | Some ws, _ when ws.State = WebSocketState.Open ->
                      let arr = msg |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
                      do! ws.SendAsync(arr,WebSocketMessageType.Text, true, CancellationToken.None)
                        |> Async.AwaitTask |> Async.Catch |> Async.Ignore
                      return! loop()
                    | Some ws, _ when ws.State = WebSocketState.Connecting ->
                      do! Async.Sleep 1000
                      return! loop()
                    | _ ->
                      websocket config.path ws
                      return! loop() }
                loop ()
                )
            mappings <- mappings |> Map.add config.name (config.customSerializers,sender.Post)
        fun dispatch ->
            dispatcher dispatch
            { new IDisposable with
                member _.Dispose() =
                    match ws.Value with
                    | Some webs, _ when webs.State = WebSocketState.Open ->
                        ws.Value <- None, true
                        use w = webs
                        w.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None)
                                |> Async.AwaitTask |> Async.Catch |> Async.Ignore |> Async.RunSynchronously
                    | _ -> ws.Value <- None, true
            }

    /// Creates a subscription to be used with `Cmd.OfSub`. That enables starting Bridge with
    /// a configuration after the `Program` has already started
    let asSubscription (this:BridgeConfig<_,_>) =
       fun dispatch -> attach this dispatch |> ignore


[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridge endpoint (program : Program<_, _, _, _>) =
        program |> Program.withSubscription (fun _ -> [["Elmish";"Bridge"], fun dispatch -> let config = (Bridge.endpoint endpoint) in Bridge.attach config dispatch])

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _, _>) =
        program |> Program.withSubscription (fun _ -> ["Elmish"::"Bridge"::(config.name |> Option.map List.singleton |> Option.defaultValue []), fun dispatch -> Bridge.attach config dispatch])


[<RequireQualifiedAccess>]
module Cmd =
    /// Creates a `Cmd` from a server message.
    let inline bridgeSend (msg:'server) : Cmd<'client> = [ fun _ -> Bridge.Send msg ]
    /// Creates a `Cmd` from a server message using a named bridge.
    let inline namedBridgeSend name (msg:'server) : Cmd<'client> = [ fun _ -> Bridge.NamedSend(name, msg) ]

[<AutoOpen>]
module RPC =

    type RPC.IReplyChannel<'T> with
    member t.Reply(v:'T) =
        Bridge.rpcSender(t.ValueId, v, None)
    member t.ReplyNamed(name, v:'T) =
        Bridge.rpcSender(t.ValueId, v, Some name)

    member t.ReplyException(v:exn) =
        Bridge.rpcSender(t.ExceptionId, v, None)
    member t.ReplyExceptionNamed(name, v:'T) =
        Bridge.rpcSender(t.ExceptionId, v, Some name)


