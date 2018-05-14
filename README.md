# Fable.Elmish.Remoting

This library creates a bridge between server and client using websockets so you can keep the same model-view-update mindset to create the server side model.

## Available Packages:

| Library  | Version |
| ------------- | ------------- |
| Fable.Elmish.Remoting.Client  | [![Nuget](https://img.shields.io/nuget/v/Fable.Elmish.Remoting.Client.svg?colorB=green)](https://www.nuget.org/packages/Fable.Elmish.Remoting.Client) |
| Fable.Elmish.Remoting.Suave  | [![Nuget](https://img.shields.io/nuget/v/Fable.Elmish.Remoting.Suave.svg?colorB=green)](https://www.nuget.org/packages/Fable.Elmish.Remoting.Suave)  |
| Fable.Elmish.Remoting.HMR  | [![Nuget](https://img.shields.io/nuget/v/Fable.Elmish.Remoting.HMR.svg?colorB=green)](https://www.nuget.org/packages/Fable.Elmish.Remoting.HMR)  |

## Why?

The MVU approach made programming client-side SPA really fun and fast for me. The idea of having a model updated in a predictable way, with a view just caring about what was in the model just clicked, and making a server-side with just consumable services made everything different and I felt like I was making two entirely different programs. I came up with this project so I can have the same fun when creating the server.

## Drawbacks

You are now just passing messages between the server and the client. Stuff like authorization, headers or cookies are not there anymore. You have a stateful connection between the client while it's open, but if it gets disconnected the server will lose all the state it had.
That can be mitigated by sending a message to the client when it reestabilishes the connection, so it knows that the server is there again. You can then send all the information that the server needs to feel like the connection was never broken in the first place.

tl;dr You have to relearn the server-side part

## How to use it?

This is Elmish on server with a bridge to the client. You can learn about Elmish [here](https://elmish.github.io/). This assumes that you know how to use Elmish on the client-side.

### Shared

I recommend to keep the messages and the endpoint on a shared file; you don't need to keep the model if you decide that you want the server and client to have different models.

```fsharp
// Messages processed on the server
type ServerMsg = 
    |...
//Messages processed on the client
type ClientMsg =
    |...
module Shared =
    let endpoint = "/socket"
```

The server and the client can return both kind of messages: `S ServerMsg` to define the server and `C ClientMsg` to the client kind. The `update` function now returns a `'model * Cmd<Msg<ServerMsg,ClientMsg>>`.

### Client

What's different? Well, now you can send messages to and get messages from the server. The `update` function that was `'msg -> 'model -> 'model * Cmd<'msg>` now is a `'client -> 'model -> 'model * Cmd<Msg<'server,'client>>` and that is incompatible with the Elmish's `mkProgram`, so you can use the bridge (`ClientProgram.updateBridge update`) to solve that problem. Then before creating the client, create the `ClientProgram` passing the `Program` to the `ClientProgram.fromProgram` function. Finally, use `ClientProgram.runAt` to run the program using the websocket endpoint. More on that later.

```fsharp
open Elmish
open Elmish.Remoting

Program.mkProgram init (ClientProgram.updateBridge update) view
|> Program.withReact "elmish-app"
|> ClientProgram.fromProgram
|> ClientProgram.runAt Shared.endpoint
```

### Server

Now you can use the MVU approach on the server, minus the V. That still is just a client thing. Create a new server using `ServerProgram.mkProgram init update`. No need for a bridge, it already expects a `'server -> 'model -> 'model * Cmd<Msg<'server,'client>>`. There's also `ServerProgram.withSubscription` that behaves the same as `Program.withSubscription`.

For use it, you need a server. There is one for Suave on the `Fable.Elmish.Remoting.Suave` package. You can pass it to the function `ServerProgram.runServerAt`. Here is how it's used:

```fsharp
open Elmish
open Elmish.Remoting

let server =
  ServerProgram.mkProgram init update
  |> ServerProgram.runServerAt Suave.server Shared.endpoint

let webPart =
  choose [
    server
    Filters.path "/" >=> Files.browseFileHome "index.html"
    Files.browseHome
    RequestErrors.NOT_FOUND "Not found!"
  ]
startWebServer config webPart
```

### HMR

The HMR module creates a new kind of message that it's not entirely compatible with the client/server, but you can use `Fable.Elmish.Remoting.HMR` functions to create a compatible `ClientProgram`:

```fsharp
open Elmish
open Elmish.Remoting
open Elmish.HMR
open Elmish.Remoting.HMR

Program.mkProgram init (ClientProgram.updateBridge update) view
|> Program.withHMR
|> Program.withReactUnoptimized "elmish-app"
|> ClientProgram.fromHMRProgram
|> ClientProgram.runAt Shared.endpoint
```

and `ServerProgram`:

```fsharp
open Elmish.Remoting
open Elmish.HMR
open Elmish.Remoting.HMR

let server =
  ServerProgram.mkProgram init update
  |> ServerProgram.withHMR
  |> ServerProgram.runServerAt Suave.server Shared.endpoint
```


## Anything more?

This documentation is on an early stage, if you have any questions feel free to open an issue or PR so we can have it in a good shape. You can check a test project [here](https://github.com/Nhowka/TestRemoting). I hope you enjoy using it!
