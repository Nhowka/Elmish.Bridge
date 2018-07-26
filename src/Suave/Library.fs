namespace Elmish.Bridge

[<RequireQualifiedAccess>]
module Suave =
    open Suave
    open Suave.Filters
    open Suave.Operators
    open Suave.Sockets
    open Suave.Sockets.Control
    open Suave.WebSocket

    /// Suave's server used by `ServerProgram.runServerAtWith` and `ServerProgram.runServerAt`
    /// Creates a `WebPart`
    let server (program : BridgeServer<'arg, 'model, 'server, 'client, WebPart>) : ServerCreator<'server, WebPart> =
        fun endpoint inboxCreator ->
            let ws (webSocket : WebSocket) _ =
                let inbox =
                    inboxCreator (fun (s : string) ->
                        let resp =
                            s
                            |> System.Text.Encoding.UTF8.GetBytes
                            |> ByteSegment
                        webSocket.send Text resp true |> Async.Ignore)

                let skt =
                    socket {
                        let mutable loop = true
                        let mutable buffer = []
                        while loop do
                            let! msg = webSocket.read()
                            match msg with
                            | Text, data, complete ->
                                buffer <- data :: buffer
                                if complete then
                                    let data =
                                        buffer
                                        |> List.rev
                                        |> Array.concat

                                    let str = UTF8.toString data
                                    let msg = program.Read str
                                    msg
                                    |> Option.iter (Choice1Of2 >> inbox.Post)
                                    buffer <- []
                            | (Close, _, _) ->
                                let emptyResponse = [||] |> ByteSegment
                                do! webSocket.send Close emptyResponse true
                                loop <- false
                            | _ -> ()
                    }

                async {
                    let! result = skt
                    program.WhenDown |> Option.iter (Choice1Of2 >> inbox.Post)
                    inbox.Post (Choice2Of2())
                    return result
                }
            path endpoint >=> handShake ws