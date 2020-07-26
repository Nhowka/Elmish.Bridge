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
    let private fableConverter = FableJsonConverter() :> JsonConverter
    let private serialize result = JsonConvert.SerializeObject(result, [| fableConverter |])
    let private settings = JsonSerializerSettings(DateParseHandling = DateParseHandling.None)
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

    let Send (server : 'Server) = sender(server, None)

    let NamedSend (name:string, server : 'Server) = sender(server, Some name)

    let internal attach (config:BridgeConfig<'Msg,'ElmishMsg>) =
        let dispatcher dispatch =
            let ws : ClientWebSocket option ref = ref None
            let rec websocket server r =
                lock ws (fun () ->
                    match !r with
                    | None ->
                        async {
                            let ws = new ClientWebSocket()
                            r := Some ws
                            try
                                do! ws.ConnectAsync(Uri(server), CancellationToken.None) |> Async.AwaitTask
                            with
                            | _ ->
                                (ws :> IDisposable).Dispose()
                                r := None
                                config.whenDown |> Option.iter dispatch}
                        |> Async.StartImmediate
                    | Some _ -> ())
            websocket config.path ws
            let recBuffer = ArraySegment(Array.zeroCreate 4096)
            let cleanSocket (webs : ClientWebSocket) =
                async {
                    do! webs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None)
                                |> Async.AwaitTask |> Async.Catch |> Async.Ignore
                    (webs :> IDisposable).Dispose()
                    ws := None
                    config.whenDown |> Option.iter dispatch
                    do! Async.Sleep (1000 * config.retryTime)
                }
            let rec receiver buffer =
              async {
                    match !ws with
                    |Some webs when webs.State = WebSocketState.Open ->
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
                                let inputJson = System.Text.Encoding.UTF8.GetString data
                                let parsedJson =
                                    try
                                        JsonConvert.DeserializeObject<'Msg>(inputJson, settings) |> Ok
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
                    | Some webs when webs.State = WebSocketState.Connecting ->
                        do! Async.Sleep (1000 * config.retryTime)
                        return! receiver []
                    | Some webs ->
                        do! cleanSocket webs
                        return! receiver []
                    | None ->
                        websocket config.path ws
                        return! receiver []
                }
            Async.StartImmediate (receiver [])
            let sender = MailboxProcessor.Start (fun mb ->
                let rec loop () = async {
                    let! (msg : string) = mb.Receive()
                    match !ws with
                    | Some ws when ws.State = WebSocketState.Open ->
                      let arr = msg |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
                      do! ws.SendAsync(arr,WebSocketMessageType.Text, true, CancellationToken.None)
                        |> Async.AwaitTask |> Async.Catch |> Async.Ignore
                      return! loop()
                    | Some ws when ws.State = WebSocketState.Connecting ->
                      do! Async.Sleep 1000
                      return! loop()
                    | _ ->
                      websocket config.path ws
                      return! loop() }
                loop ()
                )
            mappings <- mappings |> Map.add config.name (config.customSerializers,sender.Post)
        dispatcher

    /// Creates a subscription to be used with `Cmd.OfSub`. That enables starting Bridge with
    /// a  configuration after the `Program` has already started
    let asSubscription (this:BridgeConfig<_,_>) : Sub<_> =
        attach this


[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridge endpoint (program : Program<_, _, _, _>) =
        program |> Program.withSubscription (fun _ -> [Bridge.attach (Bridge.endpoint endpoint)])

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _, _>) =
        program |> Program.withSubscription (fun _ -> [Bridge.attach config])
