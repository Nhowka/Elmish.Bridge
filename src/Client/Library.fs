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

    let mappings : Map<string option, Map<string, obj -> SerializerResult> * (WebSocket option * bool) ref * (string -> (unit -> unit) -> unit)> option ref =
        match Dom.window?Elmish_Bridge_Helpers with
        | None ->
            let cell = ref (Some Map.empty)
            Dom.window?Elmish_Bridge_Helpers <- cell
            cell
        | Some m -> m

    let rpcmappings : Map<System.Guid, (string -> unit) * System.Guid> option ref =
        match Dom.window?Elmish_Bridge_RpcHelpers with
        | None ->
            let cell = ref (Some Map.empty)
            Dom.window?Elmish_Bridge_RpcHelpers <- cell
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
    member this.AddSerializer(serializer: 'a -> SerializerResult, typeOrigin: System.Type) =
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

    interface System.IDisposable with
        member t.Dispose() =
            Helpers.mappings.Value
            |> Option.defaultValue Map.empty
            |> Map.tryFind t.name
            |> Option.iter (fun (_, socket, _) ->
                let (skt,_) = socket.Value
                socket.Value <- (None, true)
                skt |> Option.iter (fun e -> e.close())
                )


    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member inline this.Attach dispatch =
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
        let wsref : (WebSocket option * bool) ref =
            Helpers.mappings.Value
            |> Option.defaultValue Map.empty
            |> Map.tryFind this.name
            |> Option.bind (fun (_, socket, _) ->
                match socket.Value with
                | None, true -> None
                | _ -> Some socket)
            |> Option.defaultValue (ref (None, false))
        let rec websocket timeout server =
            match wsref.Value with
            | Some _, _ | None, true -> ()
            | None, false ->
                let socket = WebSocket.Create server
                wsref.Value <- Some socket, false
                socket.onclose <- fun _ ->
                    let (_,closed) = wsref.Value
                    wsref.Value <- None, closed
                    this.whenDown |> Option.iter dispatch
                    if not closed then
                      Dom.window.setTimeout
                        ((fun () -> websocket timeout server), timeout, ()) |> ignore
                socket.onmessage <- fun e ->
                         let message = string e.data
                         if message.StartsWith "R" then
                            let guid = (System.Guid.Parse message.[1..36])
                            let json = message.[37..]
                            Helpers.rpcmappings.Value
                            |> Option.defaultValue Map.empty
                            |> Map.tryFind  guid
                            |> Option.iter(fun (f,og) ->
                                f json
                                Helpers.rpcmappings.Value <-
                                    Helpers.rpcmappings.Value
                                    |> Option.map( fun m  ->
                                        m
                                        |> Map.remove guid
                                        |> Map.remove og)
                                )
                         elif message.StartsWith "E" then
                            let guid = (System.Guid.Parse message.[1..])
                            Helpers.rpcmappings.Value
                            |> Option.defaultValue Map.empty
                            |> Map.tryFind  guid
                            |> Option.iter(fun (f,og) ->
                                f (Json.serialize (exn("Server couldn't process your message")))
                                Helpers.rpcmappings.Value <-
                                    Helpers.rpcmappings.Value
                                    |> Option.map( fun m  ->
                                        m
                                        |> Map.remove guid
                                        |> Map.remove og)
                                )
                         else
                             Json.tryParseNativeAs(string e.data)
                             |> function
                                | Ok msg -> msg |> this.mapping |> dispatch
                                | _ -> ()
        websocket (this.retryTime * 1000) (url.href.TrimEnd '#')
        Helpers.mappings.Value <-
            Helpers.mappings.Value
            |> Option.defaultValue Map.empty
            |> Map.add this.name
                (this.customSerializers,
                 wsref,
                 (fun e callback ->
                    match wsref.Value with
                    | Some socket, _ -> socket.send e
                    | None, _ -> callback ()))
            |> Some

type Bridge private() =

    static member private stringTuple = (TypeInfo.Tuple(fun () -> [|TypeInfo.String;TypeInfo.String|]))

    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    static member Sender(server : 'Server, bridgeName, callback, sentType: System.Type) =

        let sentTypeName = sentType.FullName.Replace('+','.')
        Helpers.mappings.Value
        |> Option.defaultValue Map.empty
        |> Map.tryFind bridgeName
        |> function
           | None -> callback ()
           | Some (m,_,s) ->
                    let serializer =
                        m
                        |> Map.tryFind sentTypeName
                        |> Option.defaultValue
                            (fun o -> Convert.serialize o (createTypeInfo sentType) |> Text)
                    let serialized =
                        match serializer server with
                        | Text e -> e
                        | Binary b -> System.Convert.ToBase64String b
                    s (Convert.serialize (sentTypeName, serialized) Bridge.stringTuple) callback

    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    static member RPCSender(guid, bridgeName, value, sentType: System.Type) =

        Helpers.mappings.Value
        |> Option.defaultValue Map.empty
        |> Map.tryFind bridgeName
        |> Option.iter
               (fun (_,_,s) ->
                    let typeInfo = createTypeInfo sentType
                    let serialized = Convert.serialize value typeInfo
                    s (Convert.serialize (sprintf "RC|%O" guid, serialized) Bridge.stringTuple) ignore)

    static member inline RPCSend(guid: System.Guid, value: 'a, ?name) =
        Bridge.RPCSender(guid, name, value, typeof<'a>)

    /// Send the message to the server
    static member inline Send(server : 'Server,?callback) =
        Bridge.Sender(server, None, defaultArg callback ignore, typeof<'Server>)

    /// Send the message to the server using a named bridge
    static member inline NamedSend(name:string, server : 'Server,?callback) =
        Bridge.Sender(server, Some name, defaultArg callback ignore, typeof<'Server>)


    static member inline AskServer(f: IReplyChannel<'T> -> 'Server ) : Async<'T> =
        Bridge.Asker(f, None, typeof<'Server>, typeof<'T> )

    static member inline AskNamedServer(f: IReplyChannel<'T> -> 'Server, name ) : Async<'T> =
        Bridge.Asker(f, Some name, typeof<'Server>, typeof<'T>  )


    static member Asker(f, bridgeName, sentType, ttype ) =
        Async.FromContinuations(fun (cont, econt, _) ->
            let guidValue = System.Guid.NewGuid()
            let guidExn = System.Guid.NewGuid()
            let typeInfoT = createTypeInfo ttype
            let typeInfoExn = createTypeInfo typeof<exn>

            let reply typeInfo cont s =
                let json = SimpleJson.parse s
                Convert.fromJsonAs json typeInfo |> unbox |> cont

            Helpers.rpcmappings.Value <-
                Helpers.rpcmappings.Value
                |> Option.defaultValue Map.empty
                |> Map.add guidExn ((fun s -> reply typeInfoExn econt s), guidValue)
                |> Map.add guidValue ((fun s -> reply typeInfoT cont s), guidExn)
                |> Some

            let sentTypeName = sentType.FullName.Replace('+','.')
            Helpers.mappings.Value
            |> Option.defaultValue Map.empty
            |> Map.tryFind bridgeName
            |> function
               | None -> econt (exn("Bridge does not exist"))
               | Some (_,_,s) ->
                    let serialized = Convert.serialize (f {ValueId = guidValue; ExceptionId = guidExn}) (createTypeInfo sentType)
                    s (Convert.serialize (sprintf "RS|%s" sentTypeName, serialized) Bridge.stringTuple) (fun () -> econt (exn("Socket is closed")))
        )

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
        this.AddSerializer(serializer, typeof<'a>)

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
        let sub dispatch =
            this.Attach dispatch
            {new System.IDisposable with member _.Dispose() = ()}
        [ ["Elmish";"Bridge"], sub ]

    /// Enables using Elmish.Bridge with any function that can receive compatible messages.
    /// This includes React hooks, Lit and Svelte
    let inline onCustomDispatcher dispatch (this:BridgeConfig<_,_>) =
        this.Attach dispatch

[<RequireQualifiedAccess>]
module Program =

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridge endpoint (program : Program<_, _, _, _>) =
        program |> Program.mapSubscription (fun prev m -> (["Elmish";"Bridge"], fun dispatch -> let config = Bridge.endpoint(endpoint) in config.Attach dispatch; config ) :: (prev m))

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let inline withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _, _>) =
       program |> Program.mapSubscription (fun prev m -> ("Elmish"::"Bridge"::(config.name |> Option.map List.singleton |> Option.defaultValue []), fun dispatch -> config.Attach dispatch; config) :: (prev m))

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

  type RPC.IReplyChannel<'T> with

    member inline t.Reply(v:'T) =
        Bridge.RPCSend(t.ValueId, v)
    member inline t.ReplyNamed(name, v:'T) =
        Bridge.RPCSend(t.ValueId, v, name)

    member inline t.ReplyException(v:exn) =
        Bridge.RPCSend(t.ExceptionId, v)
    member inline t.ReplyExceptionNamed(name, v:'T) =
        Bridge.RPCSend(t.ExceptionId, v, name)
