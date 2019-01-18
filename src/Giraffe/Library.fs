namespace Elmish.Bridge

[<RequireQualifiedAccess>]
module Giraffe =
    open System
    open Giraffe
    open FSharp.Control.Tasks.ContextInsensitive
    open System.Net.WebSockets
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Http
    open System.Threading

    /// Giraffe's server used by `ServerProgram.runServerAtWith` and `ServerProgram.runServerAt`
    /// Creates a `HttpHandler`
    let server endpoint inboxCreator : HttpHandler =
        let ws (next : HttpFunc) (ctx : HttpContext) =
            task {
                if ctx.WebSockets.IsWebSocketRequest then
                    let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
                    let (sender,closer) =
                        inboxCreator
                            (fun (s:string) ->
                            let resp =
                                s
                                |> System.Text.Encoding.UTF8.GetBytes
                                |> ArraySegment
                            webSocket.SendAsync
                                (resp, WebSocketMessageType.Text, true,
                                 CancellationToken.None) |> Async.AwaitTask)
                    let skt =
                        task {
                            let buffer = Array.zeroCreate 4096
                            let mutable loop = true
                            let mutable frame = []
                            while loop do
                                let! msg = webSocket.ReceiveAsync
                                               (ArraySegment(buffer),
                                                CancellationToken.None)
                                match msg.MessageType,
                                      buffer.[0..msg.Count - 1],
                                      msg.EndOfMessage, msg.CloseStatus with
                                | _, _, _, s when s.HasValue ->
                                    do! webSocket.CloseOutputAsync
                                            (WebSocketCloseStatus.NormalClosure,
                                             null, CancellationToken.None)
                                    loop <- false
                                | WebSocketMessageType.Text, data, complete,
                                  _ ->
                                    frame <- data :: frame
                                    if complete then
                                        frame
                                        |> List.rev
                                        |> Array.concat
                                        |> System.Text.Encoding.UTF8.GetString
                                        |> sender
                                        frame <- []
                                | _ -> ()
                        }
                    try
                        do! skt
                    with _ -> ()
                    closer()
                    return Some ctx
                else return None
            }
        route endpoint >=> ws

    /// Prepare app to use websockets
    let useWebSockets (app : IApplicationBuilder) = app.UseWebSockets(WebSocketOptions(KeepAliveInterval=TimeSpan.FromSeconds 30.))