namespace Elmish.Bridge

open System
open System.Net.WebSockets
open System.Threading
open Fabulous.Core
open Thoth.Json.Net
[<RequireQualifiedAccess>]
module internal Constants =
    [<Literal>]
    let internal socketIdentifier = "elmish_bridge_socket"

type BridgeConfig<'Msg,'ElmishMsg> =
    { path : string
      whenDown : 'ElmishMsg option
      mapping :  'Msg -> 'ElmishMsg
      retryTime : int
      name : string option}


[<RequireQualifiedAccess>]
module Bridge =
    let mutable mappings : Map<string, string -> unit> = Map.empty
    /// Create a new `BridgeConfig` with the set endpoint
    let endpoint endpoint =
        {
            path = endpoint
            whenDown = None
            mapping = id
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
            retryTime = this.retryTime
            name = this.name
        }

    let Send (server : 'Server) =
        mappings
        |> Map.tryFind Constants.socketIdentifier
        |> Option.iter(fun o -> Thoth.Json.Net.Encode.Auto.toString (0,(typeof<'Server>.FullName.Replace('+','.'), server)) |> o )

    let NamedSend (name:string, server : 'Server) =
        mappings
        |> Map.tryFind (Constants.socketIdentifier + "_" + name)
        |> Option.iter(fun o -> Thoth.Json.Net.Encode.Auto.toString (0,(typeof<'Server>.FullName.Replace('+','.'), server)) |> o )

    let internal Attach (config:BridgeConfig<'Msg,'ElmishMsg>) (program : Program<_, 'ElmishMsg, _>) =
        let decoder : Decode.Decoder<'Msg> = Decode.Auto.generateDecoder false
        let sub m =
          let dispatcher dispatch =
            let mutable ws : ClientWebSocket option ref = ref None
            let rec websocket server r =
                lock ws (fun () ->
                    match !r with
                    | None ->
                        let ws = new ClientWebSocket()
                        r := Some ws
                        ws.ConnectAsync(Uri(server), CancellationToken.None) |> Async.AwaitTask |> Async.Start
                    | Some _ -> ())
            websocket config.path ws
            let rec receiver buffer =
              let recBuffer = Array.zeroCreate 4096
              async {
                    match !ws with
                    |Some webs ->
                        let! msg = webs.ReceiveAsync(ArraySegment(recBuffer),CancellationToken.None) |> Async.AwaitTask
                        match msg.MessageType,recBuffer.[0..msg.Count-1],msg.EndOfMessage,msg.CloseStatus with
                        |_,_,_,s when s.HasValue ->
                            do! webs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None) |> Async.AwaitTask
                            (webs :> IDisposable).Dispose()
                            ws := None
                            config.whenDown |> Option.iter dispatch
                            do! Async.Sleep (1000 * config.retryTime)
                            return! receiver []
                        | WebSocketMessageType.Text, data, complete, _ ->
                            let data = data::buffer
                            if complete then
                                let data = data |> List.rev |> Array.concat
                                let str = System.Text.Encoding.UTF8.GetString data
                                let msg = Decode.fromString decoder str
                                match msg with
                                | Ok msg -> msg |> config.mapping |> dispatch
                                | Error er -> eprintfn "%s" er
                                return! receiver []
                            else
                                return! receiver data
                        | _ -> return! receiver buffer
                    | None ->
                        websocket config.path ws
                        return! receiver []
                }
            Async.Start (receiver [])
            let sender = MailboxProcessor.Start (fun mb ->
                let rec loop () = async {
                    match !ws with
                    | Some ws ->
                      let! (msg : string) = mb.Receive()
                      let arr = msg |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
                      do! ws.SendAsync(arr,WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
                      return! loop()
                    | None ->
                      websocket config.path ws
                      return! loop() }
                loop ()
                )
            match config.name with
            | None -> mappings <- mappings |> Map.add Constants.socketIdentifier sender.Post
            | Some name -> mappings <- mappings |> Map.add (Constants.socketIdentifier + "_" + name) sender.Post
          [dispatcher]
        Program.withSubscription sub program

[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridge endpoint (program : Program<_, _, _>) =
        Bridge.Attach
            { path = endpoint
              whenDown = None
              mapping = id
              retryTime = 1
              name = None} program

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _>) =
        Bridge.Attach config program


(*
open Elmish
open Fable.Core
open Fable.Import
open Fable.Core.JsInterop
open Thoth.Json

[<RequireQualifiedAccess>]
module internal Constants =
    [<Literal>]
    let internal dispatchIdentifier = "elmish_bridge_message_dispatch"
    [<Literal>]
    let internal pureDispatchIdentifier = "elmish_original_message_dispatch"

    [<Literal>]
    let internal socketIdentifier = "elmish_bridge_socket"

[<RequireQualifiedAccess>]
module internal Helpers =
    let getBaseUrl() =
        let url =
            Fable.Import.Browser.window.location.href
            |> Fable.Import.Browser.URL.Create
        url.protocol <- url.protocol.Replace("http", "ws")
        url.hash <- ""
        url

/// Configures the mode about how the endpoint is used
type UrlMode =
    | Append
    | Replace
    | Raw
    | Calculated of (string -> string -> string)

/// Creates the bridge. Takes the endpoint and an optional message to be dispatched when the connection is closed.
/// It exposes a method `Send` that can be used to send messages to the server
type BridgeConfig<'Msg,'ElmishMsg> =
    { path : string
      whenDown : 'ElmishMsg option
      mapping :  'Msg -> 'ElmishMsg
      retryTime : int
      name : string option
      urlMode : UrlMode}

    member private this.Websocket whenDown timeout server name =
        let socket = Fable.Import.Browser.WebSocket.Create server
        Browser.window?(Constants.socketIdentifier + name) <- Some socket
        socket.onclose <- fun _ ->
            whenDown |> Option.iter (fun msg ->
            !!Browser.window?(Constants.pureDispatchIdentifier + name)
            |> Option.iter (fun dispatch -> dispatch msg))
            Fable.Import.Browser.window.setTimeout
                ((fun () -> this.Websocket whenDown timeout server name), timeout, ()) |> ignore
        socket.onmessage <- fun e ->
            !!Browser.window?(Constants.dispatchIdentifier + name)
            |> Option.iter (fun dispatch ->
                 e.data
                 |> string
                 |> dispatch)

    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member this.Attach(program : Elmish.Program<_, _, 'ElmishMsg, _>, [<Inject>] ?resolverMsg: ITypeResolver<'Msg>, [<Inject>] ?resolverElmishMsg: ITypeResolver<'ElmishMsg> ) =
        let url =
            match this.urlMode with
            | Replace ->
                let url = Helpers.getBaseUrl()
                url.pathname <- this.path
                url
            | Append ->
                let url = Helpers.getBaseUrl()
                url.pathname <- url.pathname + this.path
                url
            | Calculated f ->
                let url = Helpers.getBaseUrl()
                f url.href this.path |> Fable.Import.Browser.URL.Create
            | Raw ->
                let url = Fable.Import.Browser.URL.Create this.path
                url.protocol <- url.protocol.Replace("http", "ws")
                url
        let name = this.name |> Option.map ((+) "_") |> Option.defaultValue ""
        this.Websocket (this.whenDown |> Option.map (fun e -> Thoth.Json.Encode.Auto.toString(0,e))) (this.retryTime * 1000) (url.href.TrimEnd '#') name
        let msgDecoder = Thoth.Json.Decode.Auto.generateDecoder(resolver=resolverMsg.Value)

        let elmishMsgDecoder = Thoth.Json.Decode.Auto.generateDecoder(resolver=resolverElmishMsg.Value)
        let subs model =
            (fun dispatch ->
            Browser.window?(Constants.dispatchIdentifier + name) <- Some
                                                               (Thoth.Json.Decode.fromString msgDecoder
                                                                >> Result.map this.mapping
                                                                >> (function Ok e -> dispatch e | Error _ -> ()))
            Browser.window?(Constants.pureDispatchIdentifier + name) <- Some
                                                               (Thoth.Json.Decode.fromString elmishMsgDecoder
                                                                >> (function Ok e -> dispatch e | Error _ -> ())))
            :: program.subscribe model
        { program with subscribe = subs }

type Bridge private() =
    /// Send the message to the server
    static member Send(server : 'Server, [<Inject>] ?resolver: ITypeResolver<'Server>) =
        let sentType = resolver.Value.ResolveType()
        !!Browser.window?(Constants.socketIdentifier)
        |> Option.iter
               (fun (s : Fable.Import.Browser.WebSocket) ->
               s.send (Thoth.Json.Encode.Auto.toString(0,(sentType.FullName.Replace('+','.'), server))))

    /// Send the message to the server using a named bridge
    static member NamedSend(name:string, server : 'Server, [<Inject>] ?resolver: ITypeResolver<'Server>) =
        let sentType = resolver.Value.ResolveType()
        !!Browser.window?(Constants.socketIdentifier + "_" + name)
        |> Option.iter
               (fun (s : Fable.Import.Browser.WebSocket) ->
               s.send (Thoth.Json.Encode.Auto.toString(0,(sentType.FullName.Replace('+','.'), server))))

[<RequireQualifiedAccess>]
module Bridge =

    /// Create a new `BridgeConfig` with the set endpoint
    let endpoint endpoint =
        {
            path = endpoint
            whenDown = None
            mapping = id
            retryTime = 1
            name = None
            urlMode = Replace
        }

    /// Set a message to be sent when connection is lost.
    let withWhenDown msg this =
        { this with whenDown = Some msg }

    /// Sets the mode of how the url is calculated
    /// `Replace` : sets the path to the endpoint defined
    /// `Append` : adds the endpoint to the current path
    /// `Raw`: uses the given endpoint as a complete URL
    /// `Calculated` : takes a function that given the current URL and the endpoint, calculates the complete url to the socket
    let withUrlMode mode this =
        { this with urlMode = mode }

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
            retryTime = this.retryTime
            name = this.name
            urlMode = this.urlMode
        }

[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridge endpoint (program : Program<_, _, _, _>) =
        { path = endpoint
          whenDown = None
          mapping = id
          retryTime = 1
          name = None
          urlMode = Replace}.Attach program

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _, _>) =
        config.Attach program
*)
