module Module

type IInherit1 =
    inherit IInterface

type IInherit2 =
    interface IInterface


type EmptyType() =
    interface IEmptyInterface

let e = EmptyType()
e :> IEmptyInterface |> ignore
e :> IEmptyBaseInterface |> ignore


type Type() =
    interface IInterface with
        member this.M1() = ()
        member this.M2(i: int) = ()
        member this.P1 = 1
        member this.P2 = 1

let t = Type()
t :> IInterface |> ignore
t :> IBaseInterface |> ignore
