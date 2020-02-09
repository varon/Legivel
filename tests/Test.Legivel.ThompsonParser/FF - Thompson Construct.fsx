﻿open System.Threading.Tasks
open Microsoft.FSharp.Control
open System.Threading

#I __SOURCE_DIRECTORY__ 

#time

#r @"bin/Debug/net45/Legivel.Parser.dll"
#r @"bin/Debug/net45/Test.Legivel.ThompsonParser.dll"
#r @"bin/Debug/net45/NLog.dll"

open System
open Legivel.Tokenizer
open Legivel.Utilities.RegexDSL
open Legivel.ThompsonParser
open NLog

#load "nlog.fsx"

NlogInit.With __SOURCE_DIRECTORY__ __SOURCE_FILE__

let logger = LogManager.GetLogger("*")


let ``start-of-line`` = RGP ("^", [Token.NoToken])


//[<Test>]
//let ``Colliding plains in nested Repeater X-path with one state deep``() =

OPT(OPT(RGP("B", [Token.``c-printable``]))+ RGP("A", [Token.``c-printable``])) + RGP("A", [Token.``c-printable``]) 
|>  rgxToNFA
|>  PrintIt




