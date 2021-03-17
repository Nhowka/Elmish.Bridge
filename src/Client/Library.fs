namespace Elmish.Bridge

open Browser
open Browser.Types
open Elmish
open Fable.Core
open Fable.SimpleJson
open Fable.Core.JsInterop

//Configures the transport of the custom serializer
type SerializerResult =
    | Text of string
    | Binary of byte []

//Internal use only
[<RequireQualifiedAccess>]
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
module Helpers =
    let getBaseUrl() =
        let url =
            Dom.window.location.href
            |> Url.URL.Create
        url.protocol <- url.protocol.Replace("http", "ws")
        url.hash <- ""
        url

    let mappings : Map<string option, Map<string, obj -> SerializerResult> * WebSocket option ref * (string -> (unit -> unit) -> unit)> option ref =
        match Dom.window?Elmish_Bridge_Helpers with
        | None ->
            let cell = ref (Some Map.empty)
            Dom.window?Elmish_Bridge_Helpers <- cell
            cell
        | Some m -> m

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
    member inline this.Attach() =
     let subs dispatch =
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
        let wsref : WebSocket option ref =
            !Helpers.mappings
            |> Option.defaultValue Map.empty
            |> Map.tryFind this.name
            |> Option.map (fun (_, socket, _) -> socket)
            |> Option.defaultValue (ref None)
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
                         Json.tryParseNativeAs(string e.data)
                         |> function
                            | Ok msg -> msg |> this.mapping |> dispatch
                            | _ -> ()
        websocket (this.retryTime * 1000) (url.href.TrimEnd '#')
        Helpers.mappings :=
            !Helpers.mappings
            |> Option.defaultValue Map.empty
            |> Map.add this.name
                (this.customSerializers,
                 wsref,
                 (fun e callback ->
                    match !wsref with
                    | Some socket -> socket.send e
                    | None -> callback ()))
            |> Some
     subs

type Bridge private() =
    static member private Sender(server : 'Server, bridgeName, callback, sentType: System.Type) =


        let sentTypeName = sentType.FullName.Replace('+','.')
        !Helpers.mappings
        |> Option.defaultValue Map.empty
        |> Map.tryFind bridgeName
        |> Option.iter
               (fun (m,_,s) ->
                    let serializer =
                        m
                        |> Map.tryFind sentTypeName
                        |> Option.defaultValue
                            (Json.serialize >> Text)
                    let serialized =
                        match serializer server with
                        | Text e -> e
                        | Binary b -> System.Convert.ToBase64String b
                    s (Json.serialize(sentTypeName, serialized)) callback)

    static member private RPCSender(guid, bridgeName, value, sentType: System.Type) =

        !Helpers.mappings
        |> Option.defaultValue Map.empty
        |> Map.tryFind bridgeName
        |> Option.iter
               (fun (_,_,s) ->
                    let typeInfo = createTypeInfo sentType
                    let stringType = createTypeInfo typeof<string>
                    s (Convert.serialize (sprintf "RPC|%O" guid, value) (TypeInfo.Tuple(fun () -> [|stringType;typeInfo|]))) ignore)

    static member RPCSend(guid: System.Guid, value: 'a, ?name, [<Inject>] ?resolver: ITypeResolver<'a>) =
        Bridge.RPCSender(guid, name, value, resolver.Value.ResolveType())

    /// Send the message to the server
    static member Send(server : 'Server,?callback, [<Inject>] ?resolver: ITypeResolver<'Server>) =
        Bridge.Sender(server, None, defaultArg callback ignore, resolver.Value.ResolveType())

    /// Send the message to the server using a named bridge
    static member NamedSend(name:string, server : 'Server,?callback, [<Inject>] ?resolver: ITypeResolver<'Server>) =
        Bridge.Sender(server, Some name, defaultArg callback ignore, resolver.Value.ResolveType())

[<RequireQualifiedAccess>]
module Bridge =

    /// Create a new `BridgeConfig` with the set endpoint
    let inline endpoint endpoint =
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
    let inline withWhenDown msg this =
        { this with whenDown = Some msg }

    /// Sets the mode of how the url is calculated
    /// `Replace` : sets the path to the endpoint defined
    /// `Append` : adds the endpoint to the current path
    /// `Raw`: uses the given endpoint as a complete URL
    /// `Calculated` : takes a function that given the current URL and the endpoint, calculates the complete url to the socket
    let inline withUrlMode mode this =
        { this with urlMode = mode }

    /// Set a name for this bridge if you want to have a secondary one.
    let inline withName name this =
        { this with name = Some name }

    /// Register a custom serializer
    let inline withCustomSerializer (serializer: 'a -> SerializerResult) (this:BridgeConfig<'Msg,'ElmishMsg>) =
        this.AddSerializer serializer

    /// Configure how many seconds before reconnecting when the connection is lost.
    /// Values below 1 are ignored
    let inline withRetryTime sec this =
        if sec < 1 then
            this
        else
            { this with retryTime = sec}

    /// Configure a mapping to the top-level message so the server can send an inner message
    /// That enables using just a subset of the messages on the shared project
    let inline withMapping map this =
        {
            whenDown = this.whenDown
            path = this.path
            mapping = map
            customSerializers = this.customSerializers
            retryTime = this.retryTime
            name = this.name
            urlMode = this.urlMode
        }

    /// Creates a subscription to be used with `Cmd.OfSub`. That enables starting Bridge with
    /// a configuration obtained after the `Program` has already started
    let inline asSubscription (this:BridgeConfig<_,_>) =
        this.Attach()


[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridge endpoint (program : Program<_, _, _, _>) =
        program |> Program.withSubscription (fun _ -> [Bridge.endpoint(endpoint).Attach()])

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _, _>) =
       program |> Program.withSubscription (fun _ -> [config.Attach ()])

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

[<AutoOpen>]
module RPC =

    type IReplyChannel<'T> = {
      ValueId : System.Guid
      ExceptionId : System.Guid
     }
    with

    member inline t.Reply(v:'T) =
        Bridge.RPCSend(t.ValueId, v)
    member inline t.ReplyNamed(name, v:'T) =
        Bridge.RPCSend(t.ValueId, v, name)

    member inline t.ReplyException(v:exn) =
        Bridge.RPCSend(t.ExceptionId, v)
    member inline t.ReplyExceptionNamed(name, v:'T) =
        Bridge.RPCSend(t.ExceptionId, v, name)
