namespace Elmish.Bridge

open Elmish

type internal ServerHubData<'model, 'server, 'client> =
    { Model : 'model
      ServerDispatch : Dispatch<'server>
      ClientDispatch : Dispatch<'client> }

/// Holds functions that will be used when interaction with the `ServerHub`
type ServerHubInstance<'model, 'server, 'client> =
    { Update : 'model -> unit
      Add : 'model -> Dispatch<'server> -> Dispatch<'client> -> unit
      Remove : unit -> unit }
    static member internal Empty : ServerHubInstance<'model, 'server, 'client> =
        { Add = fun _ _ _ -> ()
          Remove = ignore
          Update = ignore }

type internal ServerHubMessages<'model, 'server, 'client> =
    | ServerBroadcast of 'server
    | ClientBroadcast of 'client
    | ServerSendIf of ('model -> bool) * 'server
    | ClientSendIf of ('model -> bool) * 'client
    | GetModels of AsyncReplyChannel<'model list>
    | AddClient of System.Guid * ServerHubData<'model, 'server, 'client>
    | UpdateModel of System.Guid * 'model
    | DropClient of System.Guid

/// Holds the data of all connected clients
type ServerHub<'model, 'server, 'client>() =
    let mutable clientMappings =
        Map.empty
        |> Map.add typeof<'client>.FullName (fun (o : obj) -> o :?> 'client)
    let mutable serverMappings =
        Map.empty
        |> Map.add typeof<'server>.FullName (fun (o : obj) -> o :?> 'server)

    let mb =
        MailboxProcessor.Start(fun inbox ->
            let rec hub data =
                async {
                    let! action = inbox.Receive()
                    match action with
                    | ServerBroadcast msg ->
                        async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter
                                   (fun (_, { ServerDispatch = d }) -> msg |> d)
                        }
                        |> Async.Start
                    | ClientBroadcast msg ->
                        async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter
                                   (fun (_, { ClientDispatch = d }) -> msg |> d)
                        }
                        |> Async.Start
                    | ServerSendIf(predicate, msg) ->
                        async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter (fun (_, { Model = m;
                                                              ServerDispatch = d }) ->
                                   if predicate m then msg |> d)
                        }
                        |> Async.Start
                    | ClientSendIf(predicate, msg) ->
                        async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter (fun (_, { Model = m;
                                                              ClientDispatch = d }) ->
                                   if predicate m then msg |> d)
                        }
                        |> Async.Start
                    | GetModels ar ->
                        async {
                            data
                            |> Map.toList
                            |> List.map (fun (_, { Model = m }) -> m)
                            |> ar.Reply
                        }
                        |> Async.Start
                    | AddClient(guid, hd) ->
                        return! hub (data |> Map.add guid hd)
                    | UpdateModel(guid, model) ->
                        return! data
                                |> Map.tryFind guid
                                |> Option.map
                                       (fun hd ->
                                       data
                                       |> Map.add guid { hd with Model = model })
                                |> Option.defaultValue data
                                |> hub
                    | DropClient(guid) -> return! hub (data |> Map.remove guid)
                    return! hub data
                }
            hub Map.empty)

    /// Register the client mappings so inner messages can be transformed to the top-level `update` message
    member this.RegisterClient<'Inner, 'client>(map : 'Inner -> 'client) =
        clientMappings <- clientMappings
                          |> Map.add typeof<'Inner>.FullName
                                 (fun (o : obj) -> o :?> 'Inner |> map)
        this

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    member this.RegisterServer<'Inner, 'server>(map : 'Inner -> 'server) =
        serverMappings <- serverMappings
                          |> Map.add typeof<'Inner>.FullName
                                 (fun (o : obj) -> o :?> 'Inner |> map)
        this

    /// Send client message for all connected users
    member __.BroadcastClient<'inner>(msg : 'inner) =
        clientMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               f msg
               |> ClientBroadcast
               |> mb.Post)

    /// Send server message for all connected users
    member __.BroadcastServer<'inner>(msg : 'inner) =
        serverMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               f msg
               |> ServerBroadcast
               |> mb.Post)

    /// Send client message for all connected users if their `model` passes the predicate
    member __.SendClientIf predicate (msg : 'inner) =
        clientMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               (predicate, f msg)
               |> ClientSendIf
               |> mb.Post)

    /// Send server message for all connected users if their `model` passes the predicate
    member __.SendServerIf predicate (msg : 'inner) =
        serverMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               (predicate, f msg)
               |> ServerSendIf
               |> mb.Post)

    /// Return the model of all connected users
    member __.GetModels() = mb.PostAndReply GetModels

    member private __.Init() : ServerHubInstance<'model, 'server, 'client> =
        let guid = System.Guid.NewGuid()

        let add =
            fun model serverDispatch clientDispatch ->
                mb.Post(AddClient(guid,
                                  { Model = model
                                    ServerDispatch = serverDispatch
                                    ClientDispatch = clientDispatch }))

        let remove = fun () -> mb.Post(DropClient guid)
        let update = fun model -> mb.Post(UpdateModel(guid, model))
        { Add = add
          Remove = remove
          Update = update }

    /// Used to create a default `ServerHubInstance` that does nothing when the `ServerHub` is not set
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    static member Initialize(sh : ServerHub<'model, 'server, 'client> option) =
        sh
        |> Option.map (fun sh -> sh.Init())
        |> Option.defaultValue ServerHubInstance.Empty

type ServerCreator<'model, 'server, 'client, 'impl> = string -> ((string -> Async<unit>) -> ServerHubInstance<'model, 'server, 'client> -> MailboxProcessor<Choice<'server, unit>>) -> 'impl

/// Defines server configuration
type BridgeServer<'arg, 'model, 'server, 'client, 'impl>(endpoint : string, init, update) =
    let mutable subscribe = fun _ -> Cmd.none
    let mutable logMsg = ignore
    let mutable logPMsg = ignore
    let mutable logRegister = ignore
    let mutable logSMsg = ignore
    let mutable logInit = ignore
    let mutable logModel = ignore
    static let c = Fable.JsonConverter()

    static let s =
        let js = Newtonsoft.Json.JsonSerializer()
        js.Converters.Add c
        js

    let mutable mappings = Map.empty


    let write (o : 'client) = Newtonsoft.Json.JsonConvert.SerializeObject(o, c)
    /// Server msg passed to the `update` function when the connection is closed
    member val WhenDown : 'server option = None with get, set
    /// Registers the `ServerHub` that will be used by this socket connections
    member val ServerHub : ServerHub<'model, 'server, 'client> option = None with get, set

    member this.WithWhenDown n =
        this.WhenDown <- Some n
        this

    member this.WithServerHub sh =
        this.ServerHub <- Some sh
        this

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    member this.Register<'Inner, 'server>(map : 'Inner -> 'server) =
        let t = typeof<'Inner>
        let name = t.FullName.Replace('+','.')
        logRegister name
        mappings <- mappings
                    |> Map.add name
                           (fun (o : Newtonsoft.Json.Linq.JToken) ->
                           o.ToObject(t, s) :?> 'Inner |> map)
        this
    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial model, but can dispatch new messages at any time.
    member this.WithSubscription sub =
        let sub model =
            Cmd.batch [ subscribe model
                        sub model ]
        subscribe <- sub
        this

    /// Add a log function for the initial model
    member this.AddInitLogging log =
        let oldLogInit = logInit
        logInit <- fun m ->
            oldLogInit m
            log m
        this
    /// Add a log function for logging type names on registering
    member this.AddRegisterLogging log =
        let oldLogRegister = logRegister
        logRegister <- fun m ->
            oldLogRegister m
            log m
        this

    /// Add a log function after the model updating
    member this.AddModelLogging log =
        let oldLogModel = logModel
        logModel <- fun m ->
            oldLogModel m
            log m
        this

    /// Add a log function when receiving a new message
    member this.AddMsgLogging log =
        let oldLogMsg = logMsg
        logMsg <- fun m ->
            oldLogMsg m
            log m
        this

    /// Add a log function when receiving a raw socket message
    member this.AddSocketRawMsgLogging log =
        let oldLogSMsg = logSMsg
        logSMsg <- fun m ->
            oldLogSMsg m
            log m
        this

    /// Add a log function after parsing the raw socket message
    member this.AddSocketParsedMsgLogging log =
        let oldLogPMsg = logPMsg
        logPMsg <- fun m ->
            oldLogPMsg m
            log m
        this

    /// Trace all the operation to the console
    member this.WithConsoleTracing =
        this.AddInitLogging(eprintfn "Initial state: %A")
            .AddMsgLogging(eprintfn "New message: %A")
            .AddSocketRawMsgLogging(eprintfn "Remote message: %s")
            .AddSocketParsedMsgLogging(eprintfn "Parsed remote message: %A")
            .AddRegisterLogging(eprintfn "Type %s registered")
            .AddModelLogging(eprintfn "Updated state: %A")
    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member this.Start server (arg : 'arg) : 'impl =
        this.Register(id) |> ignore
        let inbox action hubInstance =
            MailboxProcessor.Start(fun (mb : MailboxProcessor<Choice<'server, unit>>) ->
                let clientDispatch (a : 'client) =
                    a
                    |> write
                    |> action
                    |> Async.Start

                let model, msgs = init clientDispatch arg
                logInit model
                let sub =
                    try
                        hubInstance.Add model (Choice1Of2 >> mb.Post)
                            clientDispatch
                        subscribe model
                    with _ -> Cmd.none
                sub @ msgs |> List.iter (fun sub -> sub (Choice1Of2 >> mb.Post))
                let rec loop (state : 'model) =
                    async {
                        let! msg = mb.Receive()
                        match msg with
                        | Choice1Of2 msg ->
                            logMsg msg
                            let model, msgs = update clientDispatch msg state
                            logModel model
                            msgs
                            |> List.iter
                                   (fun sub -> sub (Choice1Of2 >> mb.Post))
                            hubInstance.Update model
                            return! loop model
                        | Choice2Of2() -> return ()
                    }
                loop model)
        server endpoint inbox
    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member __.Read str =
        logSMsg str
        let (name : string, o : Newtonsoft.Json.Linq.JToken) =
            Newtonsoft.Json.JsonConvert.DeserializeObject
                (str, typeof<string * Newtonsoft.Json.Linq.JToken>, c) :?> _
        let parsed =
            mappings
            |> Map.tryFind name
            |> Option.map (fun e -> e o)
        parsed |> Option.iter logPMsg
        parsed

[<RequireQualifiedAccess>]
module Bridge =
    /// Creates a `ServerBridge`
    /// Takes an `endpoint` where the server will listen for connections
    /// a `init` : `Dispatch<'client> -> 'arg -> 'model * Cmd<'server>`
    /// and a `update` : `Dispatch<'client> -> 'server -> 'model -> 'model * Cmd<'server>`
    /// Typical program, new commands are produced by `init` and `update` along with the new state.
    let mkServer endpoint
        (init : Dispatch<'client> -> 'arg -> ('model * Cmd<'server>))
        (update : Dispatch<'client> -> 'server -> 'model -> ('model * Cmd<'server>)) =
        BridgeServer(endpoint, init, update)

    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial model, but can dispatch new messages at any time.
    let withSubscription subscribe (program : BridgeServer<_, _, _, _, _>) =
        program.WithSubscription subscribe

    /// Log changes on the model and received messages to the console
    let withConsoleTrace (program : BridgeServer<_, _, _, _, _>) =
        program.WithConsoleTracing

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    let register map (program : BridgeServer<_, _, _, _, _>) =
        program.Register map

    /// Registers the `ServerHub` that will be used by this socket connections
    let withServerHub hub (program : BridgeServer<_, _, _, _, _>) =
        program.WithServerHub hub

    /// Server msg passed to the `update` function when the connection is closed
    let whenDown msg (program : BridgeServer<_, _, _, _, _>) =
        program.WithWhenDown msg

    /// Creates a websocket loop.
    /// `arg`: argument to pass to the `init` function.
    /// `program`: program created with `mkProgram`.
    let runWith server arg (program : BridgeServer<_, _, _, _, _>) =
        program.Start (server program) arg

    /// Creates a websocket loop with `unit` for the `init` function.
    /// `program`: program created with `mkProgram`.
    let run server (program : BridgeServer<_, _, _, _, _>) =
        program.Start (server program) ()