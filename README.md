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

In progress
