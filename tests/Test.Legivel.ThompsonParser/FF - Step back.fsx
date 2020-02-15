﻿#I __SOURCE_DIRECTORY__ 

#time

#r @"bin/Debug/net45/Legivel.Parser.dll"
#r @"bin/Debug/net45/Test.Legivel.ThompsonParser.dll"
#r @"bin/Debug/net45/NLog.dll"

open Legivel.Tokenizer
open Legivel.Utilities.RegexDSL
open Legivel.ThompsonParser
open NLog

#load "nlog.fsx"

NlogInit.With __SOURCE_DIRECTORY__ __SOURCE_FILE__

let logger = LogManager.GetLogger("*")

let ``s-indent(n)`` = Repeat(RGP (HardValues.``s-space``, [Token.``t-space``]), 0)
let ``s-indent(<n)`` = Range(RGP (HardValues.``s-space``, [Token.``t-space``]), 0, -1) (* Where m < n *)

let ``s-flow-line-prefix`` = (``s-indent(n)``) + OPT(HardValues.``s-separate-in-line``)

let ``s-line-prefix Flow-in`` = ``s-flow-line-prefix``

let ``l-empty Flow-in`` = ((``s-line-prefix Flow-in``) ||| (``s-indent(<n)``)) + HardValues.``b-as-line-feed``

let ``b-l-trimmed Flow-in`` = HardValues.``b-non-content`` + OOM(``l-empty Flow-in``)


let ``b-l-folded Flow-in`` = ``b-l-trimmed Flow-in`` ||| HardValues.``b-as-space``

let ``s-flow-folded`` =
    OPT(HardValues.``s-separate-in-line``) + (``b-l-folded Flow-in``) + ``s-line-prefix Flow-in``


let ``s-double-escaped`` = ZOM(HardValues.``s-white``) + HardValues.``c-escape`` + HardValues.``b-non-content`` + ZOM(``l-empty Flow-in``) + (``s-flow-line-prefix``)

let ``s-double-break`` = (``s-double-escaped``) ||| (``s-flow-folded``)

let ``s-double-next-line`` =  
    ZOM((``s-double-break``) + HardValues.``ns-double-char`` + HardValues.``nb-ns-double-in-line``) + (``s-double-break``) |||
    OOM((``s-double-break``) + HardValues.``ns-double-char`` + HardValues.``nb-ns-double-in-line``) + ZOM(HardValues.``s-white``)


let ``nb-double-multi-line`` = HardValues.``nb-ns-double-in-line`` + ((``s-double-next-line``) ||| ZOM(HardValues.``s-white``))

let ``nb-double-text`` = ``nb-double-multi-line`` 

let ``c-double-quoted`` = HardValues.``c-double-quote`` + GRP(``nb-double-text``) + HardValues.``c-double-quote``

let nfa = ``c-double-quoted`` |> rgxToNFA

let yaml = "
\"Fun with \\\\
\\\" \\a \\b \\e \\f \\
\\n \\r \\t \\v \\0 \\
\\  \\_ \\N \\L \\P \\
\\x41 \\u0041 \\U00000041\"
"

let stream = RollingStream<_>.Create (tokenProcessor yaml) (TokenData.Create (Token.EOF) "")
stream.Position <- 1


let r = parseIt nfa stream






