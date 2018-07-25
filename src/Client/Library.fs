namespace Elmish.Bridge

open Elmish
open Fable.Core
open Fable.Import
open Fable.Core.JsInterop

[<RequireQualifiedAccess>]
module internal Constants =
    [<Literal>]
    let internal dispatchIdentifier = "elmish_bridge_message_dispatch"
    [<Literal>]
    let internal pureDispatchIdentifier = "elmish_original_message_dispatch"

    [<Literal>]
    let internal socketIdentifier = "elmish_bridge_socket"

/// Creates the bridge. Takes the endpoint and an optional message to be dispatched when the connection is closed.
/// It exposes a method `Send` that can be used to send messages to the server
type BridgeConfig<'Msg,'ElmishMsg> =
    { path : string
      whenDown : 'ElmishMsg option
      mapping :  'Msg -> 'ElmishMsg
      retryTime : int }

    [<PassGenerics>]
    member private this.Websocket whenDown timeout server =
        let socket = Fable.Import.Browser.WebSocket.Create server
        Browser.window?(Constants.socketIdentifier) <- Some socket
        socket.onclose <- fun _ ->
            whenDown |> Option.iter (fun msg ->
            !!Browser.window?(Constants.pureDispatchIdentifier)
            |> Option.iter (fun dispatch -> dispatch msg))
            Fable.Import.Browser.window.setTimeout
                (this.Websocket whenDown timeout server, timeout) |> ignore
        socket.onmessage <- fun e ->
            !!Browser.window?(Constants.dispatchIdentifier)
            |> Option.iter (fun dispatch ->
                 e.data
                 |> string
                 |> dispatch)


    [<PassGenerics>]
    member internal this.Attach(program : Elmish.Program<_, _, 'ElmishMsg, _>) =
        let url =
            Fable.Import.Browser.URL.Create
                (Fable.Import.Browser.window.location.href)
        url.protocol <- url.protocol.Replace("http", "ws")
        url.pathname <- this.path
        url.hash <- ""
        this.Websocket (this.whenDown |> Option.map JsInterop.toJson) (this.retryTime * 1000) url.href
        let subs model =
            (fun dispatch ->
            Browser.window?(Constants.dispatchIdentifier) <- Some
                                                               (JsInterop.ofJson<'Msg>
                                                                >> this.mapping
                                                                >> dispatch)
            Browser.window?(Constants.pureDispatchIdentifier) <- Some
                                                               (JsInterop.ofJson<'ElmishMsg>
                                                                >> dispatch))
            :: program.subscribe model
        { program with subscribe = subs }

[<RequireQualifiedAccess>]
module Bridge =
    /// Send the message to the server
    [<PassGenerics>]
    let Send(server : 'Server) =
        !!Browser.window?(Constants.socketIdentifier)
        |> Option.iter
               (fun (s : Fable.Import.Browser.WebSocket) ->
               s.send (JsInterop.toJson (typeof<'Server>.FullName.Replace('+','.'), server)))

    /// Create a new `BridgeConfig` with the set endpoint
    [<PassGenerics>]
    let endpoint endpoint =
        {
            path = endpoint
            whenDown = None
            mapping = id
            retryTime = 1
        }

    /// Set a message to be sent when connection is lost.
    [<PassGenerics>]
    let withWhenDown msg this =
        { this with whenDown = Some msg}

    /// Configure how many seconds before reconnecting when the connection is lost.
    /// Values below 1 are ignored
    [<PassGenerics>]
    let withRetryTime sec this =
        if sec < 1 then
            this
        else
            { this with retryTime = sec}

    /// Configure a mapping to the top-level message so the server can send an inner message
    /// That enables using just a subset of the messages on the shared project
    [<PassGenerics>]
    let withMapping map this =
        {
            whenDown = this.whenDown
            path = this.path
            mapping = map
            retryTime = this.retryTime
        }


[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    [<PassGenerics>]
    let withBridge endpoint (program : Program<_, _, 'Msg, _>) =
        { path = endpoint
          whenDown = None
          mapping = id
          retryTime = 1}.Attach program

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    [<PassGenerics>]
    let withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, 'Msg, _>) =
        config.Attach program
