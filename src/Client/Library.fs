namespace Elmish.Bridge

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

    member internal this.Attach(program : Elmish.Program<_, _, 'ElmishMsg, _>, [<Inject>] ?resolverMsg: ITypeResolver<'Msg>, [<Inject>] ?resolverElmishMsg: ITypeResolver<'ElmishMsg> ) =
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
