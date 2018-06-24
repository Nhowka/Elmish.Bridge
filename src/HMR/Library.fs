namespace Elmish.Bridge.HMR
open Elmish.HMR
open Elmish
[<RequireQualifiedAccess>]
module Bridge =
  /// Maps the `'client` message to a `HMRMsg<'client>` message
  let HMRMsgMapping = Program.UserMsg