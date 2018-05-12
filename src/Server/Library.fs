namespace Elmish.Remoting
open Elmish

module Server =
    open Newtonsoft.Json
    open Fable
    let private c = JsonConverter()
    let write o = JsonConvert.SerializeObject(o,c)
    let read<'a> str =
        JsonConvert.DeserializeObject(str,typeof<'a>,c) :?> 'a
    let createMailbox action arg (program: ServerProgram<_,_,_,_>)  =
        let model, msgs = program.init arg
        let inbox = MailboxProcessor.Start(fun (mb:MailboxProcessor<Msg<'server, 'client>>) ->
            let rec loop (state:'model) =
                async {
                    let! msg = mb.Receive()
                    match msg with
                    |S msg ->
                        let model, msgs = program.update msg state
                        msgs |> List.iter (fun sub -> sub mb.Post)
                        return! loop model
                    |C msg ->
                        do! write msg |> action
                        return! loop state}
            loop model)
        let sub =
            try
                program.subscribe model
            with _ ->
                Cmd.none
        sub @ msgs |> List.iter (fun sub -> sub inbox.Post)
        inbox
[<RequireQualifiedAccess>]
module ServerProgram =
    let mkProgram
        (init : 'arg -> 'model * Cmd<Msg<'server,'client>>)
        (update : 'server -> 'model -> 'model * Cmd<Msg<'server,'client>>) =
        {
            init = init
            update = update
            subscribe = fun _ -> Cmd.none
            onDisconnection = None
        }
    let withSubscription subscribe (program: ServerProgram<_,_,_,_>) =
        let sub model =
            Cmd.batch [ program.subscribe model
                        subscribe model ]
        { program with subscribe = sub }
    let withConsoleTrace (program: ServerProgram<_,_,_,_>) =
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
    let onDisconnected msg program =
        { program with onDisconnection = Some msg}

    let runServerAtWith server uri arg  =
        server uri arg
    let runServerAt server uri =
        server uri ()


