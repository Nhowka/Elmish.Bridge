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
    { endpoint : string
      whenDown : 'ElmishMsg option
      mapping :  'Msg -> 'ElmishMsg}

    [<PassGenerics>]
    member private this.Websocket whenDown server =
        let socket = Fable.Import.Browser.WebSocket.Create server
        Browser.window?(Constants.socketIdentifier) <- Some socket
        socket.onclose <- fun _ ->
            whenDown |> Option.iter (fun msg ->
            !!Browser.window?(Constants.pureDispatchIdentifier)
            |> Option.iter (fun dispatch -> dispatch msg))
            Fable.Import.Browser.window.setTimeout
                (this.Websocket whenDown server, 1000) |> ignore
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
        url.pathname <- this.endpoint
        url.hash <- ""
        this.Websocket (this.whenDown |> Option.map JsInterop.toJson) url.href
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
               s.send (JsInterop.toJson (typeof<'Server>.FullName, server)))

[<RequireQualifiedAccess>]
module Program =
    /// Apply the `Bridge` to be used with the program with a message to be sent when connection is lost.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    [<PassGenerics>]
    let withBridgeWhenDown endpoint whenDown (program : Program<_, _, 'Msg, _>) =
        { endpoint = endpoint
          whenDown = Some whenDown
          mapping = id }.Attach program

    /// Apply the `Bridge` to be used with the program with a mapping to receive inner messages.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    [<PassGenerics>]
    let withBridgeWithMapping endpoint (mapping:'Msg -> 'ElmishMsg) (program : Program<_, _, 'ElmishMsg, _>) =
        { endpoint = endpoint
          whenDown = None
          mapping = mapping }.Attach program

    /// Apply the `Bridge` to be used with the program with a mapping to receive inner messages
    /// and a message to be sent when connection is lost.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    [<PassGenerics>]
    let withBridgeWithMappingAndWhenDown endpoint mapping (whenDown:'ElmishMsg) (program : Program<_, _, 'ElmishMsg, _>) =
        { endpoint = endpoint
          whenDown = Some whenDown
          mapping = mapping }.Attach program

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    [<PassGenerics>]
    let withBridge endpoint (program : Program<_, _, 'Msg, _>) =
        { endpoint = endpoint
          whenDown = None
          mapping = id}.Attach program