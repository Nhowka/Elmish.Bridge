namespace Elmish.Bridge

open Browser
open Browser.Types
open Elmish
open Fable.Core
open Fable.SimpleJson

//Configures the transport of the custom serializer
type SerializerResult =
    | Text of string
    | Binary of byte []

[<RequireQualifiedAccess>]
module internal Helpers =
    let getBaseUrl() =
        let url =
            Dom.window.location.href
            |> Url.URL.Create
        url.protocol <- url.protocol.Replace("http", "ws")
        url.hash <- ""
        url

    let mutable internal mappings : Map<string option, Map<string, obj -> SerializerResult> * (string -> (unit -> unit) -> unit)> = Map.empty

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
      customSerializers: Map<string, obj -> SerializerResult>
      retryTime : int
      name : string option
      urlMode : UrlMode}

    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member this.AddSerializer(serializer: 'a -> SerializerResult, [<Inject>] ?resolver: ITypeResolver<'a>) =
        let typeOrigin = resolver.Value.ResolveType()
        let typeOriginName = typeOrigin.FullName.Replace("+",".")
        {
            whenDown = this.whenDown
            path = this.path
            mapping = this.mapping
            customSerializers =
                this.customSerializers
                |> Map.add typeOriginName (fun e -> serializer (e :?> 'a))
            retryTime = this.retryTime
            name = this.name
            urlMode = this.urlMode
        }

    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member this.Attach(program : Elmish.Program<_, _, 'ElmishMsg, _>, [<Inject>] ?resolverMsg: ITypeResolver<'Msg>) =
     let subs _ =
       [fun dispatch ->
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
                f url.href this.path |> Url.URL.Create
            | Raw ->
                let url = Browser.Url.URL.Create this.path
                url.protocol <- url.protocol.Replace("http", "ws")
                url
        let wsref : WebSocket option ref = ref None
        let rec websocket timeout server =
            match !wsref with
            |Some _ -> ()
            |None ->
                let socket = WebSocket.Create server
                wsref := Some socket
                socket.onclose <- fun _ ->
                    wsref := None
                    this.whenDown |> Option.iter dispatch
                    Dom.window.setTimeout
                        ((fun () -> websocket timeout server), timeout, ()) |> ignore
                socket.onmessage <- fun e ->
                         Json.tryParseAs(string e.data, resolverMsg.Value)
                         |> function
                            | Ok msg -> msg |> this.mapping |> dispatch
                            | _ -> ()
        websocket (this.retryTime * 1000) (url.href.TrimEnd '#')
        Helpers.mappings <-
            Helpers.mappings
            |> Map.add this.name
                (this.customSerializers,
                 (fun e callback ->
                    match !wsref with
                    | Some socket -> socket.send e
                    | None -> callback ()))]
     program |> Program.withSubscription subs

type Bridge private() =
    static member private Sender(server : 'Server, bridgeName, callback, sentType: System.Type) =


        let sentTypeName = sentType.FullName.Replace('+','.')
        Helpers.mappings
        |> Map.tryFind bridgeName
        |> Option.iter
               (fun (m,s) ->
                    let serializer =
                        m
                        |> Map.tryFind sentTypeName
                        |> Option.defaultValue
                            (SimpleJson.stringify >> Text)
                    let serialized =
                        match serializer server with
                        | Text e -> e
                        | Binary b -> System.Convert.ToBase64String b
                    s (SimpleJson.stringify(sentTypeName, serialized)) callback)
    /// Send the message to the server
    static member Send(server : 'Server,?callback, [<Inject>] ?resolver: ITypeResolver<'Server>) =
        Bridge.Sender(server, None, defaultArg callback ignore, resolver.Value.ResolveType())

    /// Send the message to the server using a named bridge
    static member NamedSend(name:string, server : 'Server,?callback, [<Inject>] ?resolver: ITypeResolver<'Server>) =
        Bridge.Sender(server, Some name, defaultArg callback ignore, resolver.Value.ResolveType())

[<RequireQualifiedAccess>]
module Bridge =

    /// Create a new `BridgeConfig` with the set endpoint
    let endpoint endpoint =
        {
            path = endpoint
            whenDown = None
            mapping = id
            customSerializers = Map.empty
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

    /// Register a custom serializer
    let inline withCustomSerializer (serializer: 'a -> SerializerResult) (this:BridgeConfig<'Msg,'ElmishMsg>) =
        this.AddSerializer serializer

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
            urlMode = this.urlMode
        }

[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridge endpoint (program : Program<_, _, _, _>) =
        Bridge.endpoint(endpoint).Attach program

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _, _>) =
        config.Attach program

[<RequireQualifiedAccess>]
module Cmd =
    /// Creates a `Cmd` from a server message.
    let inline bridgeSend (msg:'server) : Cmd<'client> = [ fun _ -> Bridge.Send msg ]
    /// Creates a `Cmd` from a server message. Dispatches the client message if the bridge is broken.
    let inline bridgeSendOr (msg:'server) (fallback:'client) : Cmd<'client> = [ fun dispatch -> Bridge.Send(msg, fun () -> dispatch fallback) ]
    /// Creates a `Cmd` from a server message using a named bridge.
    let inline namedBridgeSend name (msg:'server) : Cmd<'client> = [ fun _ -> Bridge.NamedSend(name, msg) ]
    /// Creates a `Cmd` from a server message using a named bridge. Dispatches the client message if the bridge is broken.
    let inline namedBridgeSendOr name (msg:'server) (fallback:'client) : Cmd<'client> = [ fun dispatch -> Bridge.NamedSend(name, msg, fun () -> dispatch fallback) ]
