namespace Elmish.Remoting
open Elmish

module Server =
    open Newtonsoft.Json
    open Fable
    let private c = JsonConverter()
    let private write o = JsonConvert.SerializeObject(o,c)
    let read<'a> str =
        JsonConvert.DeserializeObject(str,typeof<'a>,c) :?> 'a
    let createMailbox action hubInstance arg (program: ServerProgram<'arg,'model,'server,'originalclient,'client>)  =
        let model, msgs = program.init arg
        let msgs = msgs |> Cmd.map program.mapMsg
        let inbox = MailboxProcessor.Start(fun (mb:MailboxProcessor<Msg<'server, 'client>>) ->
            let rec loop (state:'model) =
                async {
                    let! msg = mb.Receive()
                    match msg with
                    |S msg ->
                        let model, msgs = program.update msg state
                        msgs |> Cmd.map program.mapMsg |> List.iter (fun sub -> sub mb.Post)
                        hubInstance.Update model
                        return! loop model
                    |C msg ->
                        do! write msg |> action
                        return! loop state}
            loop model)
        let sub =
            try
                hubInstance.Add model inbox.Post
                program.subscribe model |> Cmd.map program.mapMsg
            with _ ->
                Cmd.none
        sub @ msgs |> List.iter (fun sub -> sub inbox.Post)
        inbox
[<RequireQualifiedAccess>]
module ServerProgram =
    /// Creates a `ServerProgram`
    /// Takes a `init` : `'arg -> 'model * Cmd<Msg<'server,'client>>`
    /// and a `update` : `'server -> 'model -> 'model * Cmd<Msg<'server,'client>>`
    /// Typical program, new commands are produced by `init` and `update` along with the new state.
    let mkProgram
        (init : 'arg -> 'model * Cmd<Msg<'server,'client>>)
        (update : 'server -> 'model -> 'model * Cmd<Msg<'server,'client>>) =
        {
            init = init
            mapMsg = id
            update = update
            subscribe = fun _ -> Cmd.none
            serverHub = None
            onDisconnection = None
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
    let onDisconnected msg program =
        { program with onDisconnection = Some msg}

    /// Creates a websocket loop.
    /// `server`: function that creates a framework depending server with the program
    /// `uri`: websocket endpoint
    /// `arg`: argument to pass to the `init` function.
    /// `program`: program created with `mkProgram`.
    let runServerAtWith server uri arg  =
        server uri arg
    /// Creates a websocket loop with `unit` for the `init` function.
    /// `server`: function that creates a framework depending server with the program
    /// `uri`: websocket endpoint
    /// `program`: program created with `mkProgram`.
    let runServerAt server uri =
        server uri ()


