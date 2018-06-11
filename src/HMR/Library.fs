namespace Elmish.Remoting.HMR
open Elmish.HMR
open Elmish
[<RequireQualifiedAccess>]
module RemoteProgram =
  /// Maps the `'client` message to a `HMRMsg<'client>` message
  let HMRMsgMapping = Program.UserMsg