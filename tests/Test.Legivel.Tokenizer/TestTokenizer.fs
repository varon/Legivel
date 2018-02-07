﻿module TestTokenizer

open NUnit.Framework
open FsUnitTyped
open Legivel.Tokenizer
open System.Collections.Generic
open System.Configuration



let ReadStream() =
    let q = new Queue<int>([|0 .. 99|])
    (fun () -> if q.Count > 0 then q.Dequeue() else -1)


[<Test>]
let ``Test RollingStream - Simple forward read all``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1

    stream.Stream
    |>  Seq.take 100
    |>  Seq.length |> shouldEqual 100


[<Test>]
let ``Test RollingStream - Simple forward read subset``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1

    stream.Stream
    |>  Seq.take 50
    |>  Seq.length |> shouldEqual 50


[<Test>]
let ``Test RollingStream - Simple forward read beyond end``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1

    stream.Stream
    |>  Seq.truncate 110
    |>  Seq.filter (fun i -> i>=0)
    |>  Seq.length |> shouldEqual 100


[<Test>]
let ``Test RollingStream - Read - Set Position back - Read``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1

    stream.Stream
    |>  Seq.take 10
    |>  Seq.toList
    |>  shouldEqual [0 .. 9]

    stream.Position |> shouldEqual 10

    stream.Position <- 5

    stream.Stream
    |>  Seq.take 10
    |>  Seq.toList
    |>  shouldEqual [5 .. 14]


[<Test>]
let ``Test RollingStream - Set Position forward - Simple``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1

    stream.Position <- 5

    stream.Stream
    |>  Seq.take 10
    |>  Seq.toList
    |>  shouldEqual [5 .. 14]


[<Test>]
let ``Test RollingStream - Set Position forward - check nothing is lost``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1

    //  forward
    stream.Position <- 5

    stream.Stream
    |>  Seq.take 10
    |>  ignore 

    //  back to beginning
    stream.Position <- 0

    stream.Stream
    |>  Seq.take 10
    |>  Seq.toList
    |>  shouldEqual [0 .. 9]


[<Test>]
let ``Test RollingStream - Peek n - check non-consequence``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1
    let peekList = stream.Peek(5)
    stream.Stream |> Seq.take 5 |> Seq.toList |> shouldEqual peekList


[<Test>]
let ``Test RollingStream - Peek() - check non-consequence``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1
    let peek = stream.Peek()
    stream.Stream |> Seq.head |> shouldEqual peek


[<Test>]
let ``Test RollingStream - Take n``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1
    stream.Take(5) |> shouldEqual [0 .. 4]


[<Test>]
let ``Test RollingStream - Takek()``() =
    let stream = RollingStream<_>.Create (ReadStream()) -1
    stream.Take() |> shouldEqual 0


[<Test>]
let ``Test Tokenizer - Flow Sequence - simple text``() =
    let yaml = "
- Mark McGwire
- Sammy Sosa
- Ken Griffey"

    let stream = RollingStream<_>.Create (tokenProcessor yaml) (TokenData.Create (Token.EOF) "")
    stream.Stream
    |>  Seq.takeWhile (fun e -> e.Token <> Token.EOF)
    |>  Seq.toList
    |>  List.map TokenData.token
    |>  shouldEqual [
        Token.NewLine; Token.``t-hyphen`` ; Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``;  
        Token.NewLine; Token.``t-hyphen`` ; Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``; 
        Token.NewLine; Token.``t-hyphen`` ; Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``; 
    ]

[<Test>]
let ``Test Tokenizer - Block Sequence - simple text``() =
    let yaml = "[ Mark McGwire, Sammy Sosa, Ken Griffey ]"

    let stream = RollingStream<_>.Create (tokenProcessor yaml) (TokenData.Create (Token.EOF) "")
    stream.Stream
    |>  Seq.takeWhile (fun e -> e.Token <> Token.EOF)
    |>  Seq.toList
    |>  List.map TokenData.token
    |>  shouldEqual [
        Token.``t-square-bracket-start``; 
        Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``; Token.``t-comma``; 
        Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``; Token.``t-comma``; 
        Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``; 
        Token.``t-space``;  Token.``t-square-bracket-end``; 
    ]


[<Test>]
let ``Test Tokenizer - Flow Sequence - numbers``() =
    let yaml = "
- 5
- 10
- -9"

    let stream = RollingStream<_>.Create (tokenProcessor yaml) (TokenData.Create (Token.EOF) "")
    stream.Stream
    |>  Seq.takeWhile (fun e -> e.Token <> Token.EOF)
    |>  Seq.toList
    |>  List.map TokenData.token
    |>  shouldEqual [
        Token.NewLine; Token.``t-hyphen`` ; Token.``t-space``; Token.``ns-dec-digit``; 
        Token.NewLine; Token.``t-hyphen`` ; Token.``t-space``; Token.``ns-dec-digit``; 
        Token.NewLine; Token.``t-hyphen`` ; Token.``t-space``; Token.``c-printable``;
    ]

[<Test>]
let ``Test Tokenizer - Yaml directives``() =
    let yaml = "
%YAML 1.2
%TAG foo someurl
%RESERVED somestuff"

    let stream = RollingStream<_>.Create (tokenProcessor yaml) (TokenData.Create (Token.EOF) "")
    stream.Stream
    |>  Seq.takeWhile (fun e -> e.Token <> Token.EOF)
    |>  Seq.toList
    |>  List.map TokenData.token
    |>  shouldEqual [
        Token.NewLine; Token.``t-percent`` ; Token.``ns-yaml-directive``; Token.``t-space``; Token.``ns-dec-digit``; Token.``c-printable``; Token.``ns-dec-digit``
        Token.NewLine; Token.``t-percent`` ; Token.``ns-tag-directive``; Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``
        Token.NewLine; Token.``t-percent`` ; Token.``ns-reserved-directive``; Token.``t-space``; Token.``c-printable``;
    ]


[<Test>]
let ``Test Tokenizer - Document/Directives end``() =
    let yaml = "
---
time: 20:03:20
player: Sammy Sosa
...
---
time: 20:03:47
player: Sammy Sosa
...
"
    let stream = RollingStream<_>.Create (tokenProcessor yaml) (TokenData.Create (Token.EOF) "")
    stream.Stream
    |>  Seq.takeWhile (fun e -> e.Token <> Token.EOF)
    |>  Seq.toList
    |>  List.map TokenData.token
    |>  shouldEqual [
        Token.NewLine; Token.``c-directives-end``
        Token.NewLine; Token.``c-printable`` ; Token.``t-colon``; Token.``t-space``; Token.``ns-dec-digit``; Token.``t-colon``; Token.``ns-dec-digit``; Token.``t-colon``;Token.``ns-dec-digit``
        Token.NewLine; Token.``c-printable`` ; Token.``t-colon``; Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``
        Token.NewLine; Token.``c-document-end``

        Token.NewLine; Token.``c-directives-end``
        Token.NewLine; Token.``c-printable`` ; Token.``t-colon``; Token.``t-space``; Token.``ns-dec-digit``; Token.``t-colon``; Token.``ns-dec-digit``; Token.``t-colon``;Token.``ns-dec-digit``
        Token.NewLine; Token.``c-printable`` ; Token.``t-colon``; Token.``t-space``; Token.``c-printable``; Token.``t-space``; Token.``c-printable``
        Token.NewLine; Token.``c-document-end``
        Token.NewLine
    ]


[<Test>]
let ``Test Tokenizer - Directives end - borderline case``() =
    let yaml = "--"
    let stream = RollingStream<_>.Create (tokenProcessor yaml) (TokenData.Create (Token.EOF) "")
    stream.Stream
    |>  Seq.takeWhile (fun e -> e.Token <> Token.EOF)
    |>  Seq.toList
    |>  List.map TokenData.token
    |>  shouldEqual [Token.``c-printable``]

