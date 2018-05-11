namespace Elmish.Remoting

type Msg<'server,'client> =
    | S of 'server
    | C of 'client
