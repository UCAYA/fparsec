﻿// Copyright (c) Stephan Tolksdorf 2008-2009
// License: BSD-style. See accompanying documentation.

module FParsec.Error

open System.Diagnostics

open FParsec.Internals

#nowarn "61" // "The containing type can use 'null' as a representation value for its nullary union case. This member will be compiled as a static member."

[<DebuggerDisplay("{GetDebuggerDisplay(),nq}")>]
type ErrorMessage =
     | Expected       of string
     | Unexpected     of string
     | Message        of string
     | CompoundError  of string * Pos * ErrorMessageList
     | BacktrackPoint of Pos * ErrorMessageList
     | OtherError     of System.IComparable
     with
        // the default DebuggerDisplay generated by the F# compiler doesn't use the DebuggerDisplay for ErrorMessageList
        member internal t.GetDebuggerDisplay() =
            match t with
            | Expected(str)   -> "Expected \"" + str + "\""
            | Unexpected(str) -> "Unexpected \"" + str + "\""
            | Message(str)    -> "Message \"" + str + "\""
            | OtherError o    -> "OtherError(" + o.ToString() + ")"
            | CompoundError(str, pos, error)
                -> "CompoundError(\"" + str + "\", " + pos.ToString() + ", " + ErrorMessageList.GetDebuggerDisplay(error) + ")"
            | BacktrackPoint(pos, error)
                -> "BacktrackPoint(" + pos.ToString() + ", " + ErrorMessageList.GetDebuggerDisplay(error) + ")"


and [<CompilationRepresentation(CompilationRepresentationFlags.UseNullAsTrueValue);
      CustomEquality; CustomComparison>]
    [<DebuggerTypeProxy(typeof<ErrorMessageListDebugView>);
      DebuggerDisplay("{ErrorMessageList.GetDebuggerDisplay(this),nq}")>]
    ErrorMessageList =
    | AddErrorMessage of ErrorMessage * ErrorMessageList
    | NoErrorMessages
    with
        // compiled as static member, so valid for t = null
        member t.ToSet() =
            let rec convert (set: Set<_>) xs =
                match xs with
                | NoErrorMessages -> set
                | AddErrorMessage(hd, tl) ->
                    match hd with
                    // filter out empty messages
                    | Expected(s)
                    | Unexpected(s)
                    | Message(s)
                      when isNullOrEmpty s
                        -> convert set tl
                    | _ -> convert (set.Add(hd)) tl

            convert (Set.empty<ErrorMessage>) t

        static member OfSeq(msgs: seq<ErrorMessage>) =
            msgs |> Seq.fold (fun lst msg -> AddErrorMessage(msg, lst)) NoErrorMessages

        // compiled as instance member, but F#'s operator '=' will handle the null cases
        override t.Equals(value: obj) =
            referenceEquals (t :> obj) value
            ||  match value with
                | null -> false
                | :? ErrorMessageList as other -> compare (t.ToSet()) (other.ToSet()) = 0
                | _ -> false

        interface System.IComparable with
            member t.CompareTo(value: obj) = // t can't be null (i.e. NoErrorMessages)
                match value with
                | null -> 1
                | :? ErrorMessageList as msgs -> compare (t.ToSet()) (msgs.ToSet())
                | _ -> invalidArg "value" "Object must be of type ErrorMessageList."

        override t.GetHashCode() = t.ToSet().GetHashCode()

        static member internal GetDebuggerDisplay(msgs: ErrorMessageList) =
            match msgs with
            | NoErrorMessages -> "NoErrorMessages"
            | _ -> match List.ofSeq (Seq.truncate 3 (msgs.ToSet())) with
                   | []         -> "NoErrorMessages"
                   | [e1]       -> "[" + e1.GetDebuggerDisplay() + "]"
                   | [e1; e2]   -> "[" + e1.GetDebuggerDisplay() + "; " + e2.GetDebuggerDisplay() + "; ...]"
                   | e1::e2::tl -> "[" + e1.GetDebuggerDisplay() + "; " + e2.GetDebuggerDisplay() + "]"



and [<Sealed>]
    ErrorMessageListDebugView(msgs: ErrorMessageList) =
        [<DebuggerBrowsable(DebuggerBrowsableState.RootHidden)>]
        member t.Items = msgs.ToSet() |> Set.toArray

let expectedError   label = AddErrorMessage(Expected(label), NoErrorMessages)
let unexpectedError label = AddErrorMessage(Unexpected(label), NoErrorMessages)
let messageError    msg   = AddErrorMessage(Message(msg), NoErrorMessages)
let otherError      obj   = AddErrorMessage(OtherError(obj), NoErrorMessages)

let backtrackError (state: State<'u>) error =
    match error with
    | AddErrorMessage(BacktrackPoint _, NoErrorMessages) -> error
    | _ -> AddErrorMessage(BacktrackPoint(state.Pos, error), NoErrorMessages)

let compoundError label (state: State<'u>) error =
    match error with
    | AddErrorMessage(BacktrackPoint(pos2, error2), NoErrorMessages)
        -> AddErrorMessage(CompoundError(label, pos2, error2), NoErrorMessages)
    | _ -> AddErrorMessage(CompoundError(label, state.Pos, error), NoErrorMessages)

let rec concatErrorMessages msgs msgs2 =
    match msgs2 with
    | AddErrorMessage(hd, tl) -> concatErrorMessages (AddErrorMessage(hd, msgs)) tl
    | NoErrorMessages         -> msgs

let
#if NOINLINE
#else
    inline
#endif
           mergeErrors msgs1 msgs2 =
    match msgs1 with
    | NoErrorMessages -> msgs2
    | _ -> concatErrorMessages msgs1 msgs2

let
#if NOINLINE
#else
    inline
#endif
           mergeErrorsIfNeeded (oldState: State<'u>) oldError (newState: State<'u>) newError =
    if isNull oldError || newState != oldState then newError
    else concatErrorMessages oldError newError

let
#if NOINLINE
#else
    inline
#endif
           mergeErrorsIfNeeded3 (veryOldState: State<'u>) veryOldError
                                (oldState: State<'u>) oldError
                                (newState: State<'u>) newError =
    let error = mergeErrorsIfNeeded veryOldState veryOldError oldState oldError
    mergeErrorsIfNeeded oldState error newState newError


let printErrorLine (stream: CharStream) (index: int64) (tw: System.IO.TextWriter) (indent: string) (columnWidth: int) =
    let iter = stream.Seek(index)
    if index > iter.Index then
        invalidArg "index ""The given index lies beyond the end of the given CharStream."
    let space = columnWidth - indent.Length
    if space > 0 then
       let leftBound = max (index - int64 space) stream.BeginIndex
       let off = int32 (index - leftBound)
       let s = iter.Advance(-off).Read(2*space)
       let newlineChars = [|'\r'; '\n'; '\u0085'; '\u000C'; '\u2028'; '\u2029'|]
       let lineBegin = if off > 0 then s.LastIndexOfAny(newlineChars, off - 1) + 1 else 0
       let lineEnd   = let i = s.IndexOfAny(newlineChars, lineBegin) in if i >= 0 then i else s.Length
       let space = if lineEnd > off then space else space - 1
       let left      = max (min (lineEnd   - space) (off - space/2)) lineBegin
       let right     = min (max (lineBegin + space) (off + (space - space/2))) lineEnd
       if right > left then
           fprintfn tw "%s%s"  indent (s.Substring(left, right - left).Replace('\t', ' '))
           fprintf  tw "%s%s^" indent (new string(' ', off - left))
           if    not iter.IsEndOfStream
              || columnWidth - (indent.Length + off - left + 1) < 14
           then tw.WriteLine()
           else tw.WriteLine("(end of input)")
       elif not iter.IsEndOfStream && columnWidth - indent.Length >= 23 then
           fprintfn tw "%sError on an empty line." indent
       elif iter.IsEndOfStream && columnWidth - indent.Length >= 22 then
           fprintfn tw "%sError at end of input." indent
       else
           tw.WriteLine(if columnWidth >= indent.Length then indent else "")
    else
        tw.WriteLine(if columnWidth = indent.Length then indent else "")

/// the default position printer
let internal printPosition (tw: System.IO.TextWriter) (p: Pos) (indent: string) (columnWidth: int) =
    fprintfn tw "%sError in %s%sLn: %i Col: %i"
                indent p.StreamName (if System.String.IsNullOrEmpty(p.StreamName) then "" else ": ") p.Line p.Column

[<Sealed>]
type ParserError(pos: Pos, error: ErrorMessageList) =
    do if isNull pos then nullArg "pos"

    member t.Pos = pos
    member T.Error = error

    override t.ToString() =
        use sw = new System.IO.StringWriter()
        t.WriteTo(sw)
        sw.ToString()

    member t.ToString(streamWhereErrorOccurred: CharStream) =
        use sw = new System.IO.StringWriter()
        t.WriteTo(sw, streamWhereErrorOccurred = streamWhereErrorOccurred)
        sw.ToString()

    member t.WriteTo(textWriter: System.IO.TextWriter,
                     ?positionPrinter: System.IO.TextWriter -> Pos -> string -> int -> unit,
                     ?columnWidth: int, ?initialIndention: string, ?indentionIncrement: string,
                     ?streamWhereErrorOccurred: CharStream) =
        let tw = textWriter
        let positionPrinter = defaultArg positionPrinter printPosition
        let positionPrinter = match streamWhereErrorOccurred with
                              | None        -> positionPrinter
                              | Some stream ->
                                  let originalStreamName = t.Pos.StreamName
                                  fun tw pos indent columnWidth ->
                                      positionPrinter tw pos indent columnWidth
                                      if pos.StreamName = originalStreamName then
                                          printErrorLine stream pos.Index tw indent columnWidth
        let columnWidth     = defaultArg columnWidth 79
        let ind             = defaultArg initialIndention ""
        let indIncrement    = defaultArg indentionIncrement "  "

        let rec printMessages (pos: Pos) (msgs: ErrorMessageList) ind =
            positionPrinter tw pos ind columnWidth
            let nra() = new ResizeArray<_>()
            let expectedA, unexpectedA, messageA, compoundA, backtrackA = nra(), nra(), nra(), nra(), nra()
            let mutable otherCount = 0
            for msg in msgs.ToSet() do // iterate over ordered unique messages
                match msg with
                | Expected s   -> expectedA.Add(s)
                | Unexpected s -> unexpectedA.Add(s)
                | Message s    -> messageA.Add(s)
                | OtherError obj -> otherCount <- otherCount + 1
                | CompoundError (s, pos2, msgs2) ->
                    if not (System.String.IsNullOrEmpty(s)) then expectedA.Add(s)
                    compoundA.Add((s, pos2, msgs2))
                | BacktrackPoint (pos2, msgs2) ->
                    backtrackA.Add((pos2, msgs2))
            let printArray title (a: ResizeArray<string>) (sep: string) =
                fprintf tw "%s%s: " ind title
                let n = a.Count
                for i = 0 to n - 3 do
                    fprintf tw "%s, " a.[i]
                if n > 1 then fprintf tw "%s%s" a.[n - 2] sep
                if n > 0 then fprintf tw "%s" a.[n - 1]
                fprintfn tw ""
            if expectedA.Count > 0 then
                printArray "Expecting" expectedA " or "
            if unexpectedA.Count > 0 then
                printArray "Unexpected" unexpectedA " and "
            if messageA.Count > 0 then
                let ind =  if expectedA.Count > 0 || unexpectedA.Count > 0 then
                               fprintfn tw "%sOther errors:" ind;
                               ind + indIncrement
                           else ind
                for m in messageA do
                    fprintfn tw "%s%s" ind m
            for s, pos2, msgs2 in compoundA do
                fprintfn tw ""
                fprintfn tw "%s%s could not be parsed because:" ind s
                printMessages pos2 msgs2 (ind + indIncrement)
            for pos2, msgs2 in backtrackA do
                fprintfn tw ""
                fprintfn tw "%sThe parser backtracked after:" ind
                printMessages pos2 msgs2 (ind + indIncrement)

            if    expectedA.Count = 0 && unexpectedA.Count = 0 && messageA.Count = 0
               && compoundA.Count = 0 && backtrackA.Count = 0
            then
                fprintfn tw "%sUnknown error(s)" ind
        printMessages pos error ind

    override t.Equals(value: obj) =
        referenceEquals (t :> obj) value
        ||  match value with
            | null -> false
            | :? ParserError as other -> t.Pos = other.Pos && t.Error = other.Error
            | _ -> false

    override t.GetHashCode() = t.Pos.GetHashCode() ^^^ t.Error.GetHashCode()

    interface System.IComparable with
        member t.CompareTo(value) =
            match value with
            | null -> 1
            | :? ParserError as other ->
                if isNotNull t.Pos then
                    let r = t.Pos.CompareTo(other.Pos)
                    if r <> 0 then r
                    else compare error other.Error
                elif isNull other.Pos then compare error other.Error
                else -1
            | _ -> invalidArg "value" "Object must be of type ParserError."

let _raiseInfiniteLoopException (name: string) (state: State<'u>) =
    failwith (concat4 (state.Pos.ToString()) ": The combinator '" name "' was applied to a parser that succeeds without consuming input and without changing the parser state in any other way. (If no exception had been raised, the combinator likely would have entered an infinite loop.)")
