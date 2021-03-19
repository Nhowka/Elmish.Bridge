namespace Elmish.Bridge

[<AutoOpen>]
module RPC =
    type IReplyChannel<'T> = {
      ValueId : System.Guid
      ExceptionId : System.Guid
    }
