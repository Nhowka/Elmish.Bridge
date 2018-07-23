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
    let server (program: BridgeServer<'arg,'model,'server,'client,HttpHandler>) : ServerCreator<'model, 'server, 'client, HttpHandler> =
      fun endpoint inboxCreator ->
        let ws (next:HttpFunc) (ctx:HttpContext) =
          task {
            if ctx.WebSockets.IsWebSocketRequest then
                let hi = ServerHub.Initialize program.ServerHub
                let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()

                let inbox = inboxCreator (fun s ->
                            let resp = s |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
                            webSocket.SendAsync(resp,WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask) hi
                let skt =
                  task {
                    let buffer = Array.zeroCreate 4096
                    let mutable loop = true
                    let mutable frame = []
                    while loop do
                        let! msg = webSocket.ReceiveAsync(ArraySegment(buffer), CancellationToken.None )
                        match msg.MessageType,buffer.[0..msg.Count-1],msg.EndOfMessage,msg.CloseStatus with
                        |_,_,_,s when s.HasValue ->
                            do! webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None)
                            loop <- false
                        |WebSocketMessageType.Text, data, complete, _ ->
                            frame <- data :: frame
                            if complete then
                                let data = frame |> List.rev |> Array.concat
                                let str = System.Text.Encoding.UTF8.GetString data
                                let msg = program.Read str
                                msg |> Option.iter(Choice1Of2 >> inbox.Post)
                                frame <- []
                        | _ -> ()
                  }
                do! skt
                program.WhenDown |> Option.iter (Choice1Of2 >> inbox.Post)
                hi.Remove ()
                return Some ctx
            else
                return None
            }
        route endpoint >=> ws

    /// Prepare app to use websockets
    let useWebSockets (app:IApplicationBuilder) =
        app.UseWebSockets()