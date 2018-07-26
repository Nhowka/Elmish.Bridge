# Elmish.Bridge

Formely Elmish.Remoting. This library creates a bridge between server and client using websockets so you can keep the same model-view-update mindset to create the server side model.

## Available Packages:

| Library  | Version |
| -------- | ------- |
| Elmish.Bridge.Client  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Client.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Client) |
| Elmish.Bridge.Suave  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Suave.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Suave)  |
| Elmish.Bridge.Giraffe  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Giraffe.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Giraffe)  |

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

### Client

What's different? Well, now you can send messages to and get messages from the server. For it, just send the message using `Bridge.Send`. Simple as that. But before that, you need to enable the bridge on your Elmish `Program`, preferably before anything else so it can inject the incoming messages at the right place.

```fsharp
open Elmish
open Elmish.Bridge

Program.mkProgram init update view
|> Program.withBridge Shared.endpoint
|> Program.withReact "elmish-app"

```

### Server

Now you can use the MVU approach on the server, minus the V. That still is just a client thing. For easy usage, the `init` and `update` function first argument is now a `Dispatch<ClientMsg>`. Just call that function with the client message and it will be sent there.

The server has a little more configuration to do, but that enables the client to be very simple to use while being compatible with the vanilla Elmish. A problem that exists is that the client can send any kind of message, not just the expected message that the `update` listens to. Because of that, you need register every type that you want to be listening and how to get to the original top-level message.

Imagine that you have the following model:

```fsharp
type FirstInnerMsg =
  | FIA
  | FIB
type SecondInnerMsg =
  | SIA
  | SIB
type OuterMsg = // That's the one the update expects
  | SomeMsg
  | First of FirstInnerMsg
  | Second of SecondInnerMsg
```

`OuterMsg` is registered by default, but to enable the client to do `Bridge.Send FIA` or `Bridge.Send SIB` all you need to do is:

```fsharp
let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.register First // First is a function (FirstInnerMsg -> OuterMsg)
  |> Bridge.register Second // Any ('a -> OuterMsg) will work
  |> Bridge.run (* server implementation here*)
```

As for the server implementation, there is one for Suave on the `Elmish.Bridge.Suave` package and another for Giraffe/Saturn on the `Elmish.Bridge.Giraffe` package.

- Suave

```fsharp
open Elmish
open Elmish.Bridge

let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.run Suave.server

let webPart =
  choose [
    server
    Filters.path "/" >=> Files.browseFileHome "index.html"
  ]
startWebServer config webPart
```

- Giraffe

```fsharp
open Elmish
open Elmish.Bridge

let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.run Giraffe.server

let webApp =
  choose [
    server
    route "/" >=> htmlFile "/pages/index.html" ]

let configureApp (app : IApplicationBuilder) =
  app
    .UseWebSockets()
    .UseGiraffe webApp

let configureServices (services : IServiceCollection) =
  services.AddGiraffe() |> ignore

WebHostBuilder()
  .UseKestrel()
  .Configure(Action<IApplicationBuilder> configureApp)
  .ConfigureServices(configureServices)
  .Build()
  .Run()
```

- Saturn

```fsharp
open Elmish
open Elmish.Bridge

let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.run Suave.server

let webApp =
  choose [
    server
    route "/" >=> htmlFile "/pages/index.html" ]

let app =
  application {
    router server
    disable_diagnostics
    app_config Giraffe.useWebSockets
    url uri
  }

run app
```

### ServerHub

WebSockets are even better when you use that communication power to send messages between clients. No library would be complete without help to broadcast messages or send a message to an specific client. That's where the `ServerHub` comes in! It is a very simple class that worries about keeping the information about all the connected clients so you don't have to.

Create a new `ServerHub` and register all the mappings that you will use by using:

```fsharp
let hub =
  ServerHub()
    .RegisterServer(First)
    .RegisterServer(Second)
    .RegisterClient(ClientMsg)

```

then you can use it on your server:

```fsharp
Bridge.mkServer Shared.endpoint init update
|> Bridge.withServerHub hub
|> Bridge.run server
```

It has three functions:
* `Broadcast(Server/Client)`

    Send a message to anyone connected:
    ```fsharp
      hub.BroadcastClient (NewMessage "Hello, everyone!")
    ```

* `Send(Server/Client)If`

    Send a message to anyone whose model satisfies a predicate.
    ```fsharp
      hub.SendClientIf (function {Gender = Female} -> true | _ -> false) (NewMessage "Hello, ladies!")
    ```

* `GetModels`

    Gets a list with all models of all connected clients.
    ```fsharp
      let users = hub.GetModels() |> List.map (function {Name = n} -> n)
      hub.BroadcastClient (ConnectedUsers users)
    ```

These functions were enough when creating a simple chat, but let me know if you feel limited having only them!

## Other configuration

Besides `Program.withBridge`, there is `Program.withBridgeConfig` that can configure some aspects:

- As the endpoint is mandatory, create the `BridgeConfig` using `Bridge.endpoint`

- For defining a time in seconds to reconnect, chain the config with `Bridge.withRetryTime`

- For defining a message to be sent to the client when the connection is lost, chain the config with `Bridge.withWhenDown`

- For defining a mapping so the server can send a different message to the client, chain the config with `Bridge.withMapping`. More on that on the next section.

## Minimizing shared messages

You can share just a sub-set of the messages between client and server. The message type used by the first argument of `init` and `update` functions on the server is what will be sent, so on the client you can add a mapping from that type for the type used on the client's `init`/`update`.

Imagine you have the following model:

```fsharp
type BridgeAware =
  | Hello of string

type SomeOtherMessage =
  | InnerBridge of BridgeAware
  | ...

type TopLevel =
  | SomeMsg of SomeOtherMessage
  | ...

```

You can make the server functions like this:

```fsharp
let init (clientDispatch:Dispatch<BridgeAware>) () =
  clientDispatch (Hello "I came from the server!")
  someModel,someCmds
```

And configure the client like this:

```fsharp
Program.mkProgram init update view
|> Program.withBridgeConfig(
    Bridge.endpoint Shared.endpoint
    |> Bridge.withMapping  (InnerBridge>>SomeMsg))
|> Program.withReact "elmish-app"
```

That way, only the `BridgeAware` type needs to be on the shared file

## Webpack caveat

When using the development mode of Webpack, usually a proxy is defined so the server calls can be redirected to the right place. That proxy doesn't work for websockets by default. To enable them, use `ws: true` when configuring the endpoint.

Example:

```
devServer: {
    proxy: {
      '/api/*': {
        target: 'http://localhost:' + port,
        changeOrigin: true
      },
      '/socket': {
        target: 'http://localhost:' + port,
        ws: true
     }
    },
    contentBase: "./public",
    hot: true,
    inline: true
  }
```

## Anything more?

This documentation is on an early stage, if you have any questions feel free to open an issue or PR so we can have it in a good shape. You can check a test project [here](https://github.com/Nhowka/TestRemoting). I hope you enjoy using it!
