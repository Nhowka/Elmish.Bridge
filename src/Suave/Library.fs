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
    let server endpoint inboxCreator : WebPart=
        let ws (webSocket : WebSocket) _ =
            let (sender,closer) =
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
                                buffer
                                |> List.rev
                                |> Array.concat
                                |> UTF8.toString
                                |> sender
                                buffer <- []
                        | (Close, _, _) ->
                            let emptyResponse = [||] |> ByteSegment
                            do! webSocket.send Close emptyResponse true
                            loop <- false
                        | _ -> ()
                }

            async {
                let! result = skt
                closer()
                return result
            }
        path endpoint >=> handShake ws