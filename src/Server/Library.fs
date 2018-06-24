namespace Elmish.Bridge
open Elmish

type internal ServerHubData<'model, 'server, 'client> = {
    Model : 'model
    Dispatch : Dispatch<Msg<'server,'client>>
}

/// Holds functions that will be used when interaction with the `ServerHub`
type ServerHubInstance<'model, 'server, 'client> = {
    Update : 'model -> unit
    Add : 'model -> Dispatch<Msg<'server, 'client>> -> unit
    Remove : unit -> unit
}

type internal ServerHubMessages<'model, 'server, 'client> =
    | Broadcast of Msg<'server,'client>
    | SendIf of ('model -> bool) *  Msg<'server,'client>
    | GetModels of AsyncReplyChannel<'model list>
    | AddClient of System.Guid * ServerHubData<'model, 'server, 'client>
    | UpdateModel of System.Guid * 'model
    | DropClient of System.Guid
/// Holds the data of all connected clients
type ServerHub<'model, 'server, 'client>() =
    let mb =
        MailboxProcessor.Start(
           fun inbox ->
                let rec hub data =
                    async {
                        let! action = inbox.Receive()
                        match action with
                        | Broadcast msg ->
                          async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter
                                (fun (_,{Dispatch = d}) -> msg |> d  ) } |> Async.Start
                        | SendIf(predicate, msg) ->
                          async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter
                                (fun (_,{Model = m; Dispatch = d}) ->
                                    if predicate m then
                                        msg |> d  ) } |> Async.Start
                        | GetModels ar ->
                          async {
                            data
                            |> Map.toList
                            |> List.map (fun (_,{Model = m})-> m )
                            |> ar.Reply } |> Async.Start
                        | AddClient(guid, hd) ->
                            return! hub (data |> Map.add guid hd)
                        | UpdateModel(guid,model) ->
                            let hd = data |> Map.tryFind guid
                            match hd with
                            |Some hd -> return! hub (data |> Map.add guid {hd with Model = model})
                            |None -> return! hub data
                        | DropClient(guid) ->
                            return! hub (data |> Map.remove guid)
                        return! hub data
                    }
                hub Map.empty)

    /// Creates a new `ServerHub`
    static member New() : ServerHub<'a,'b,'c> = ServerHub()
    /// Send message for all connected users
    member __.Broadcast(msg) =
        mb.Post (Broadcast msg)
    /// Send message for all connected users if their `model` passes the predicate
    member __.SendIf =
        fun predicate msg ->
            mb.Post (SendIf (predicate,msg))
    /// Return the model of all connected users
    member __.GetModels() =
        mb.PostAndReply GetModels
    member private __.Init() : ServerHubInstance<'model, 'server, 'client> =
        let guid = System.Guid.NewGuid()

        let add =
            fun model dispatch ->
                mb.Post (AddClient (guid,{Model=model;Dispatch=dispatch}))
        let remove =
            fun () ->
                mb.Post (DropClient guid)
        let update =
            fun model ->
                mb.Post (UpdateModel (guid,model))

        {Add = add; Remove=remove; Update=update}
    /// Used to create a default `ServerHubInstance` that does nothing when the `ServerHub` is not set
    static member Initialize(sh:ServerHub<'model, 'server, 'client> option)  =
        match sh with
        |None ->
          {
            Add = fun _ _ -> ()
            Remove = ignore
            Update = ignore}
        |Some sh -> sh.Init()

/// Defines server configuration
type ServerProgram<'arg, 'model, 'server, 'client, 'impl> = {
    init : 'arg -> 'model * Cmd<Msg<'server,'client>>
    update : 'server -> 'model -> 'model * Cmd<Msg<'server,'client>>
    subscribe : 'model -> Cmd<Msg<'server,'client>>
    serverHub : ServerHub<'model, 'server, 'client> option
    whenDown : 'server option
    server : ServerProgram<'arg, 'model, 'server, 'client, 'impl> -> 'arg -> 'impl
    endpoint : string
}

module Server =
    open Newtonsoft.Json
    open Fable

    type SysMessage<'server,'client> =
        | Dispose
        | Msg of Msg<'server,'client>
    let private c = JsonConverter()
    let private write o = JsonConvert.SerializeObject(o,c)
    let read<'a> str =
        JsonConvert.DeserializeObject(str,typeof<'a>,c) :?> 'a
    let createMailbox action hubInstance arg (program: ServerProgram<'arg,'model,'server,'client, 'impl>)  =
        let model, msgs = program.init arg
        let inbox = MailboxProcessor.Start(fun (mb:MailboxProcessor<SysMessage<'server, 'client>>) ->
            let rec loop (state:'model) =
                async {
                    let! msg = mb.Receive()
                    match msg with
                    | Dispose ->
                        return ()
                    | Msg (S msg) ->
                        let model, msgs = program.update msg state
                        msgs |> List.iter (fun sub -> sub (Msg >> mb.Post))
                        hubInstance.Update model
                        return! loop model
                    | Msg (C msg) ->
                        do! write msg |> action
                        return! loop state}
            loop model)
        let sub =
            try
                hubInstance.Add model (Msg >> inbox.Post)
                program.subscribe model
            with _ ->
                Cmd.none
        sub @ msgs |> List.iter (fun sub -> sub (Msg >> inbox.Post))
        inbox
[<RequireQualifiedAccess>]
module Bridge =
    /// Creates a `ServerProgram`
    /// Takes a `init` : `'arg -> 'model * Cmd<Msg<'server,'client>>`
    /// and a `update` : `'server -> 'model -> 'model * Cmd<Msg<'server,'client>>`
    /// Typical program, new commands are produced by `init` and `update` along with the new state.
    let mkServer
        (server: ServerProgram<'arg, 'model, 'server, 'client, 'impl> -> 'arg -> 'impl)
        (init : 'arg -> 'model * Cmd<Msg<'server,'client>>)
        (update : 'server -> 'model -> 'model * Cmd<Msg<'server,'client>>) =
        {
            init = init
            update = update
            subscribe = fun _ -> Cmd.none
            serverHub = None
            whenDown = None
            server = server
            endpoint = ""
        }
    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial model, but can dispatch new messages at any time.
    let withSubscription subscribe (program: ServerProgram<_,_,_,_,_>) =
        let sub model =
            Cmd.batch [ program.subscribe model
                        subscribe model ]
        { program with subscribe = sub }
    /// Trace all the updates to the console
    let withConsoleTrace (program: ServerProgram<_,_,_,_,_>) =
        let traceInit arg =
            let initModel,cmd = program.init arg
            eprintfn "Initial state: %A" initModel
            initModel,cmd

        let traceUpdate msg model =
            eprintfn "New message: %A" msg
            let newModel,cmd = program.update msg model
            eprintfn "Updated state: %A" newModel
            newModel,cmd

        { program with
            init = traceInit
            update = traceUpdate }
    /// Registers the `ServerHub` that will be used by this socket connections
    let withServerHub hub program = {
        program with serverHub = Some hub
    }

    /// Server msg passed to the `updated` function when the connection is closed
    let whenDown msg program =
        { program with whenDown = Some msg}

    /// Creates a websocket loop.
    /// `server`: function that creates a framework depending server with the program
    /// `uri`: websocket endpoint
    /// `arg`: argument to pass to the `init` function.
    /// `program`: program created with `mkProgram`.
    let runAtWith uri arg program =
        let program = { program with endpoint = uri }
        program.server program arg
    /// Creates a websocket loop with `unit` for the `init` function.
    /// `server`: function that creates a framework depending server with the program
    /// `uri`: websocket endpoint
    /// `program`: program created with `mkProgram`.
    let runAt uri program =
        let program = { program with endpoint = uri }
        program.server program ()

[<AutoOpen>]
module CE =

    type ServerBuilder<'arg, 'model, 'server, 'client, 'impl>(server: ServerProgram<'arg, 'model, 'server, 'client, 'impl> -> 'arg -> 'impl, init, update) =
        let zero : ServerProgram<'arg, 'model, 'server, 'client, 'impl> =
          {
            init = init
            update = update
            server = server
            subscribe = fun _ -> Cmd.none
            serverHub = None
            whenDown = None
            endpoint = ""
          }
        member __.Yield(_) = zero
        member __.Zero() = zero
        [<CustomOperation("sub")>]
        member __.Subscribe(sp:ServerProgram<'arg,'model,'server,'client, 'impl>, subscribe) =
            let sub model =
                Cmd.batch [ sp.subscribe model
                            subscribe model ]
            { sp with subscribe = sub }
        [<CustomOperation("serverHub")>]
        member __.ServerHub(sp:ServerProgram<'arg,'model,'server,'client, 'impl>, sh) =
            { sp with serverHub = Some sh}
        [<CustomOperation("whenDown")>]
        member __.WhenDown(sp:ServerProgram<'arg,'model,'server,'client, 'impl>, whenDown) =
            { sp with whenDown = whenDown }
        [<CustomOperation("at")>]
        member __.RunAt(sp:ServerProgram<'arg,'model,'server,'client, 'impl>,uri) =
            { sp with endpoint = uri }
        [<CustomOperation("runWith")>]
        member __.RunWith(sp:ServerProgram<'arg,'model,'server,'client, 'impl>,arg) =
            sp.server sp arg
        member __.Run(impl:'impl) = impl
        member __.Run(sp:ServerProgram<unit,'model,'server,'client, 'impl>) =
             sp.server sp ()