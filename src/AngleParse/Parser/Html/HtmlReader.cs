﻿namespace AngleParse
{
  using AngleParse.Extensions;
  using System;
  using System.Collections.Generic;
  using System.Collections;
  using System.Xml;

  /// <summary>
  /// Performs the tokenization of the source code. Follows the tokenization algorithm at:
  /// http://www.w3.org/html/wg/drafts/html/master/syntax.html
  /// </summary>
  public sealed class HtmlReader : BaseTokenizer
    , IEnumerator<HtmlNode>
    , IEnumerable<HtmlNode>
    , IXmlLineInfo
  {
    #region Fields

    private readonly HtmlEntityService _resolver;
    private String _lastStartTag;
    private TextPosition _position;
    private int _svgDepth = -1;
    private int _mathMlDepth = -1;
    private HtmlNode _current;

    #endregion

    #region Events

    /// <summary>
    /// Fired in case of a parse error.
    /// </summary>
    public event EventHandler<HtmlErrorEvent> Error;

    #endregion

    #region ctor

    /// <summary>
    /// See 8.2.4 Tokenization
    /// </summary>
    /// <param name="source">The source code manager.</param>
    /// <param name="resolver">The entity resolver to use.</param>
    public HtmlReader(TextSource source) : base(source)
    {
      State = HtmlParseMode.PCData;
      IsAcceptingCharacterData = false;
      IsStrictMode = false;
      _lastStartTag = String.Empty;
      _resolver = HtmlEntityService.Resolver;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Current node on which the enumerator is positioned
    /// </summary>
    public HtmlNode Current { get { return _current; } }

    /// <summary>
    /// Gets or sets if CDATA sections are accepted.
    /// </summary>
    public Boolean IsAcceptingCharacterData { get; set; }

    /// <summary>
    /// Gets or sets the current parse mode.
    /// </summary>
    public HtmlParseMode State { get; set; }

    /// <summary>
    /// Gets or sets if strict mode is used.
    /// </summary>
    public Boolean IsStrictMode { get; set; }

    object IEnumerator.Current { get { return _current; } }
    int IXmlLineInfo.LineNumber { get { return _current.Position.Line; } }
    int IXmlLineInfo.LinePosition { get { return _current.Position.Column; } }

    #endregion

    #region Methods

    /// <summary>
    /// Returns the next node
    /// </summary>
    public HtmlNode NextNode()
    {
      Read();
      return _current;
    }

    /// <summary>
    /// Positions the reader at the next node.
    /// </summary>
    /// <returns>Returns <c>true</c> if more nodes are available, <c>false</c> otherwise</returns>
    public bool Read()
    {
      var current = Advance();
      _position = GetCurrentPosition();

      if (current != Symbols.EndOfFile)
      {
        _current = default(HtmlNode);
        switch (State)
        {
          case HtmlParseMode.PCData:
            _current = Data(current);
            break;
          case HtmlParseMode.RCData:
            _current = RCData(current);
            break;
          case HtmlParseMode.Plaintext:
            _current = Plaintext(current);
            break;
          case HtmlParseMode.Rawtext:
            _current = Rawtext(current);
            break;
          case HtmlParseMode.Script:
            _current = ScriptData(current);
            break;
        }

        var tag = _current as HtmlTagNode;
        if (_svgDepth < 0
          && tag != null
          && _current.Type == HtmlTokenType.StartTag
          && _current.Value.Is(TagNames.Svg))
        {
          _svgDepth = 0;
          _current = HtmlForeign.SvgConfig(tag);
          return true;
        }
        else if (_svgDepth >= 0 && tag != null)
        {
          switch (tag.Type)
          {
            case HtmlTokenType.StartTag:
              if (!tag.IsSelfClosing)
                _svgDepth++;
              _current = HtmlForeign.SvgConfig(tag);
              return true;
            case HtmlTokenType.EndTag:
              _svgDepth--;
              break;
          }
        }
        else if (_mathMlDepth < 0
          && tag != null
          && _current.Type == HtmlTokenType.StartTag
          && _current.Value.Is(TagNames.Math))
        {
          _mathMlDepth = 0;
          _current = HtmlForeign.MathMlConfig(tag);
          return true;
        }
        else if (_mathMlDepth >= 0 && tag != null)
        {
          switch (tag.Type)
          {
            case HtmlTokenType.StartTag:
              if (!tag.IsSelfClosing)
                _mathMlDepth++;
              _current = HtmlForeign.MathMlConfig(tag);
              return true;
            case HtmlTokenType.EndTag:
              _mathMlDepth--;
              break;
          }
        }

        return true;
      }

      _current = NewEof(acceptable: true);
      return false;
    }

    internal void RaiseErrorOccurred(HtmlParseError code, TextPosition position)
    {
      var handler = Error;

      if (IsStrictMode)
      {
        var message = "Error while parsing the provided HTML document.";
        throw new HtmlParseException(code.GetCode(), message, position);
      }
      else if (handler != null)
      {
        var errorEvent = new HtmlErrorEvent(code, position);
        handler.Invoke(this, errorEvent);
      }
    }

    #endregion

    #region Data

    /// <summary>
    /// See 8.2.4.1 Data state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode Data(Char c)
    {
      return c == Symbols.LessThan ? TagOpen(Advance()) : DataText(c);
    }

    private HtmlNode DataText(Char c)
    {
      while (true)
      {
        switch (c)
        {
          case Symbols.LessThan:
          case Symbols.EndOfFile:
            Back();
            return NewCharacter();

          case Symbols.Ampersand:
            AppendCharacterReference(Advance());
            break;

          case Symbols.Null:
            RaiseErrorOccurred(HtmlParseError.Null);
            break;

          default:
            StringBuffer.Append(c);
            break;
        }

        c = Advance();
      }
    }

    #endregion

    #region Plaintext

    /// <summary>
    /// See 8.2.4.7 PLAINTEXT state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode Plaintext(Char c)
    {
      while (true)
      {
        switch (c)
        {
          case Symbols.Null:
            AppendReplacement();
            break;

          case Symbols.EndOfFile:
            Back();
            return NewCharacter();

          default:
            StringBuffer.Append(c);
            break;
        }

        c = Advance();
      }
    }

    #endregion

    #region RCData

    /// <summary>
    /// See 8.2.4.3 RCDATA state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode RCData(Char c)
    {
      return c == Symbols.LessThan ? RCDataLt(Advance()) : RCDataText(c);
    }

    private HtmlNode RCDataText(Char c)
    {
      while (true)
      {
        switch (c)
        {
          case Symbols.Ampersand:
            AppendCharacterReference(Advance());
            break;

          case Symbols.LessThan:
          case Symbols.EndOfFile:
            Back();
            return NewCharacter();

          case Symbols.Null:
            AppendReplacement();
            break;

          default:
            StringBuffer.Append(c);
            break;
        }

        c = Advance();
      }
    }

    /// <summary>
    /// See 8.2.4.11 RCDATA less-than sign state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode RCDataLt(Char c)
    {
      if (c == Symbols.Solidus)
      {
        // See 8.2.4.12 RCDATA end tag open state
        c = Advance();

        if (c.IsUppercaseAscii())
        {
          StringBuffer.Append(Char.ToLowerInvariant(c));
          return RCDataNameEndTag(Advance());
        }
        else if (c.IsLowercaseAscii())
        {
          StringBuffer.Append(c);
          return RCDataNameEndTag(Advance());
        }
        else
        {
          StringBuffer.Append(Symbols.LessThan).Append(Symbols.Solidus);
          return RCDataText(c);
        }
      }
      else
      {
        StringBuffer.Append(Symbols.LessThan);
        return RCDataText(c);
      }
    }

    /// <summary>
    /// See 8.2.4.13 RCDATA end tag name state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode RCDataNameEndTag(Char c)
    {
      while (true)
      {
        var token = CreateIfAppropriate(c);

        if (token != null)
        {
          return token;
        }
        else if (c.IsUppercaseAscii())
        {
          StringBuffer.Append(Char.ToLowerInvariant(c));
        }
        else if (c.IsLowercaseAscii())
        {
          StringBuffer.Append(c);
        }
        else
        {
          StringBuffer.Insert(0, Symbols.LessThan).Insert(1, Symbols.Solidus);
          return RCDataText(c);
        }

        c = Advance();
      }
    }

    #endregion

    #region Rawtext

    /// <summary>
    /// See 8.2.4.5 RAWTEXT state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode Rawtext(Char c)
    {
      return c == Symbols.LessThan ? RawtextLT(Advance()) : RawtextText(c);
    }

    private HtmlNode RawtextText(Char c)
    {
      while (true)
      {
        switch (c)
        {
          case Symbols.LessThan:
          case Symbols.EndOfFile:
            Back();
            return NewCharacter();

          case Symbols.Null:
            AppendReplacement();
            break;

          default:
            StringBuffer.Append(c);
            break;
        }

        c = Advance();
      }
    }

    /// <summary>
    /// See 8.2.4.14 RAWTEXT less-than sign state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode RawtextLT(Char c)
    {
      if (c == Symbols.Solidus)
      {
        // See 8.2.4.15 RAWTEXT end tag open state
        c = Advance();

        if (c.IsUppercaseAscii())
        {
          StringBuffer.Append(Char.ToLowerInvariant(c));
          return RawtextNameEndTag(Advance());
        }
        else if (c.IsLowercaseAscii())
        {
          StringBuffer.Append(c);
          return RawtextNameEndTag(Advance());
        }
        else
        {
          StringBuffer.Append(Symbols.LessThan).Append(Symbols.Solidus);
          return RawtextText(c);
        }
      }
      else
      {
        StringBuffer.Append(Symbols.LessThan);
        return RawtextText(c);
      }
    }

    /// <summary>
    /// See 8.2.4.16 RAWTEXT end tag name state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode RawtextNameEndTag(Char c)
    {
      while (true)
      {
        var token = CreateIfAppropriate(c);

        if (token != null)
        {
          return token;
        }
        else if (c.IsUppercaseAscii())
        {
          StringBuffer.Append(Char.ToLowerInvariant(c));
        }
        else if (c.IsLowercaseAscii())
        {
          StringBuffer.Append(c);
        }
        else
        {
          StringBuffer.Insert(0, Symbols.LessThan).Insert(1, Symbols.Solidus);
          return RawtextText(c);
        }

        c = Advance();
      }
    }

    #endregion

    #region CDATA

    /// <summary>
    /// See 8.2.4.68 CDATA section state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode CharacterData(Char c)
    {
      while (true)
      {
        if (c == Symbols.EndOfFile)
        {
          Back();
          break;
        }
        else if (c == Symbols.SquareBracketClose && ContinuesWithSensitive("]]>"))
        {
          Advance(2);
          break;
        }
        else
        {
          StringBuffer.Append(c);
          c = Advance();
        }
      }

      return NewCharacter();
    }

    /// <summary>
    /// See 8.2.4.69 Tokenizing character references
    /// </summary>
    /// <param name="c">The next input character.</param>
    /// <param name="allowedCharacter">The additionally allowed character if there is one.</param>
    private void AppendCharacterReference(Char c, Char allowedCharacter = Symbols.Null)
    {
      if (c.IsSpaceCharacter() || c == Symbols.LessThan || c == Symbols.EndOfFile || c == Symbols.Ampersand || c == allowedCharacter)
      {
        Back();
        StringBuffer.Append(Symbols.Ampersand);
      }
      else
      {
        var entity = default(String);

        if (c == Symbols.Num)
        {
          entity = GetNumericCharacterReference(Advance());
        }
        else
        {
          entity = GetLookupCharacterReference(c, allowedCharacter);
        }

        if (entity == null)
        {
          StringBuffer.Append(Symbols.Ampersand);
        }
        else
        {
          StringBuffer.Append(entity);
        }
      }
    }

    private String GetNumericCharacterReference(Char c)
    {
      var exp = 10;
      var basis = 1;
      var num = 0;
      var nums = new List<Int32>();
      var isHex = c == 'x' || c == 'X';

      if (isHex)
      {
        exp = 16;

        while ((c = Advance()).IsHex())
        {
          nums.Add(c.FromHex());
        }
      }
      else
      {
        while (c.IsDigit())
        {
          nums.Add(c.FromHex());
          c = Advance();
        }
      }

      for (var i = nums.Count - 1; i >= 0; i--)
      {
        num += nums[i] * basis;
        basis *= exp;
      }

      if (nums.Count == 0)
      {
        Back(2);

        if (isHex)
        {
          Back();
        }

        RaiseErrorOccurred(HtmlParseError.CharacterReferenceWrongNumber);
        return null;
      }

      if (c != Symbols.Semicolon)
      {
        RaiseErrorOccurred(HtmlParseError.CharacterReferenceSemicolonMissing);
        Back();
      }

      if (HtmlEntityService.IsInCharacterTable(num))
      {
        RaiseErrorOccurred(HtmlParseError.CharacterReferenceInvalidCode);
        return HtmlEntityService.GetSymbolFromTable(num);
      }
      else if (HtmlEntityService.IsInvalidNumber(num))
      {
        RaiseErrorOccurred(HtmlParseError.CharacterReferenceInvalidNumber);
        return Symbols.Replacement.ToString();
      }
      else if (HtmlEntityService.IsInInvalidRange(num))
      {
        RaiseErrorOccurred(HtmlParseError.CharacterReferenceInvalidRange);
      }

      return num.ConvertFromUtf32();
    }

    private String GetLookupCharacterReference(Char c, Char allowedCharacter)
    {
      var entity = default(String);
      var start = InsertionPoint - 1;
      var reference = new Char[32];
      var index = 0;
      var chr = CurrentChar;

      do
      {
        if (chr == Symbols.Semicolon || !chr.IsName())
        {
          break;
        }

        reference[index++] = chr;
        chr = Advance();
      }
      while (chr != Symbols.EndOfFile && index < 31);

      if (chr == Symbols.Semicolon)
      {
        reference[index] = Symbols.Semicolon;
        var value = new String(reference, 0, index + 1);
        entity = _resolver.GetSymbol(value);
      }

      while (entity == null && index > 0)
      {
        var value = new String(reference, 0, index--);
        entity = _resolver.GetSymbol(value);

        if (entity == null)
        {
          Back();
        }
      }

      chr = CurrentChar;

      if (chr != Symbols.Semicolon)
      {
        if (allowedCharacter != Symbols.Null && (chr == Symbols.Equality || chr.IsAlphanumericAscii()))
        {
          if (chr == Symbols.Equality)
          {
            RaiseErrorOccurred(HtmlParseError.CharacterReferenceAttributeEqualsFound);
          }

          InsertionPoint = start;
          return null;
        }

        Back();
        RaiseErrorOccurred(HtmlParseError.CharacterReferenceNotTerminated);
      }

      return entity;
    }

    #endregion

    #region Tags

    /// <summary>
    /// See 8.2.4.8 Tag open state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode TagOpen(Char c)
    {
      if (c == Symbols.Solidus)
      {
        return TagEnd(Advance());
      }
      else if (c.IsLowercaseAscii())
      {
        StringBuffer.Append(c);
        return TagName(NewTagOpen());
      }
      else if (c.IsUppercaseAscii())
      {
        StringBuffer.Append(Char.ToLowerInvariant(c));
        return TagName(NewTagOpen());
      }
      else if (c == Symbols.ExclamationMark)
      {
        return MarkupDeclaration(Advance());
      }
      else if (c != Symbols.QuestionMark)
      {
        State = HtmlParseMode.PCData;
        RaiseErrorOccurred(HtmlParseError.AmbiguousOpenTag);
        StringBuffer.Append(Symbols.LessThan);
        return DataText(c);
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.BogusComment);
        return BogusComment(c);
      }
    }

    /// <summary>
    /// See 8.2.4.9 End tag open state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode TagEnd(Char c)
    {
      if (c.IsLowercaseAscii())
      {
        StringBuffer.Append(c);
        return TagName(NewTagClose());
      }
      else if (c.IsUppercaseAscii())
      {
        StringBuffer.Append(Char.ToLowerInvariant(c));
        return TagName(NewTagClose());
      }
      else if (c == Symbols.GreaterThan)
      {
        State = HtmlParseMode.PCData;
        RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
        return Data(Advance());
      }
      else if (c == Symbols.EndOfFile)
      {
        Back();
        RaiseErrorOccurred(HtmlParseError.EOF);
        StringBuffer.Append(Symbols.LessThan).Append(Symbols.Solidus);
        return NewCharacter();
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.BogusComment);
        return BogusComment(c);
      }
    }

    /// <summary>
    /// See 8.2.4.10 Tag name state
    /// </summary>
    /// <param name="tag">The current tag token.</param>
    private HtmlNode TagName(HtmlTagNode tag)
    {
      while (true)
      {
        var c = Advance();

        if (c == Symbols.GreaterThan)
        {
          tag.Value = FlushBuffer();
          return EmitTag(tag);
        }
        else if (c.IsSpaceCharacter())
        {
          tag.Value = FlushBuffer();
          return ParseAttributes(tag);
        }
        else if (c == Symbols.Solidus)
        {
          tag.Value = FlushBuffer();
          return TagSelfClosing(tag);
        }
        else if (c.IsUppercaseAscii())
        {
          StringBuffer.Append(Char.ToLowerInvariant(c));
        }
        else if (c == Symbols.Null)
        {
          AppendReplacement();
        }
        else if (c != Symbols.EndOfFile)
        {
          StringBuffer.Append(c);
        }
        else
        {
          return NewEof();
        }
      }
    }

    /// <summary>
    /// See 8.2.4.43 Self-closing start tag state
    /// </summary>
    /// <param name="tag">The current tag token.</param>
    private HtmlNode TagSelfClosing(HtmlTagNode tag)
    {
      switch (Advance())
      {
        case Symbols.GreaterThan:
          tag.IsSelfClosing = true;
          return EmitTag(tag);
        case Symbols.EndOfFile:
          return NewEof();
        default:
          RaiseErrorOccurred(HtmlParseError.ClosingSlashMisplaced);
          Back();
          return ParseAttributes(tag);
      }
    }

    /// <summary>
    /// See 8.2.4.45 Markup declaration open state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode MarkupDeclaration(Char c)
    {
      if (ContinuesWithSensitive("--"))
      {
        Advance();
        return CommentStart(Advance());
      }
      else if (ContinuesWithInsensitive(TagNames.Doctype))
      {
        Advance(6);
        return Doctype(Advance());
      }
      else if (IsAcceptingCharacterData && ContinuesWithSensitive(Keywords.CData))
      {
        Advance(6);
        return CharacterData(Advance());
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.UndefinedMarkupDeclaration);
        return BogusComment(c, true);
      }
    }

    #endregion

    #region Comments

    /// <summary>
    /// See 8.2.4.44 Bogus comment state
    /// </summary>
    /// <param name="c">The current character.</param>
    private HtmlNode BogusComment(Char c, bool hadExclamation = false)
    {
      StringBuffer.Clear();

      var downlevelRevealedConditional = hadExclamation && c == '[';

      while (true)
      {
        switch (c)
        {
          case Symbols.GreaterThan:
            break;
          case Symbols.EndOfFile:
            Back();
            break;
          case Symbols.Null:
            c = Symbols.Replacement;
            goto default;
          default:
            StringBuffer.Append(c);
            c = Advance();
            continue;
        }

        State = HtmlParseMode.PCData;
        var result = NewComment();
        result.DownlevelRevealedConditional = downlevelRevealedConditional;
        return result;
      }
    }

    /// <summary>
    /// See 8.2.4.46 Comment start state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode CommentStart(Char c)
    {
      StringBuffer.Clear();

      switch (c)
      {
        case Symbols.Minus:
          return CommentDashStart(Advance()) ?? Comment(Advance());
        case Symbols.Null:
          AppendReplacement();
          return Comment(Advance());
        case Symbols.GreaterThan:
          State = HtmlParseMode.PCData;
          RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
          break;
        case Symbols.EndOfFile:
          RaiseErrorOccurred(HtmlParseError.EOF);
          Back();
          break;
        default:
          StringBuffer.Append(c);
          return Comment(Advance());
      }

      return NewComment();
    }

    /// <summary>
    /// See 8.2.4.47 Comment start dash state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode CommentDashStart(Char c)
    {
      switch (c)
      {
        case Symbols.Minus:
          return CommentEnd(Advance());
        case Symbols.Null:
          RaiseErrorOccurred(HtmlParseError.Null);
          StringBuffer.Append(Symbols.Minus).Append(Symbols.Replacement);
          return Comment(Advance());
        case Symbols.GreaterThan:
          State = HtmlParseMode.PCData;
          RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
          break;
        case Symbols.EndOfFile:
          RaiseErrorOccurred(HtmlParseError.EOF);
          Back();
          break;
        default:
          StringBuffer.Append(Symbols.Minus).Append(c);
          return Comment(Advance());
      }

      return NewComment();
    }

    /// <summary>
    /// See 8.2.4.48 Comment state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode Comment(Char c)
    {
      while (true)
      {
        switch (c)
        {
          case Symbols.Minus:
            var result = CommentDashEnd(Advance());

            if (result != null)
            {
              return result;
            }

            break;
          case Symbols.EndOfFile:
            RaiseErrorOccurred(HtmlParseError.EOF);
            Back();
            return NewComment();
          case Symbols.Null:
            AppendReplacement();
            break;
          default:
            StringBuffer.Append(c);
            break;
        }

        c = Advance();
      }
    }

    /// <summary>
    /// See 8.2.4.49 Comment end dash state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode CommentDashEnd(Char c)
    {
      switch (c)
      {
        case Symbols.Minus:
          return CommentEnd(Advance());
        case Symbols.EndOfFile:
          RaiseErrorOccurred(HtmlParseError.EOF);
          Back();
          return NewComment();
        case Symbols.Null:
          RaiseErrorOccurred(HtmlParseError.Null);
          c = Symbols.Replacement;
          break;
      }

      StringBuffer.Append(Symbols.Minus).Append(c);
      return null;
    }

    /// <summary>
    /// See 8.2.4.50 Comment end state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode CommentEnd(Char c)
    {
      while (true)
      {
        switch (c)
        {
          case Symbols.GreaterThan:
            State = HtmlParseMode.PCData;
            return NewComment();
          case Symbols.Null:
            RaiseErrorOccurred(HtmlParseError.Null);
            StringBuffer.Append(Symbols.Minus).Append(Symbols.Replacement);
            return null;
          case Symbols.ExclamationMark:
            RaiseErrorOccurred(HtmlParseError.CommentEndedWithEM);
            return CommentBangEnd(Advance());
          case Symbols.Minus:
            RaiseErrorOccurred(HtmlParseError.CommentEndedWithDash);
            StringBuffer.Append(Symbols.Minus);
            break;
          case Symbols.EndOfFile:
            RaiseErrorOccurred(HtmlParseError.EOF);
            Back();
            return NewComment();
          default:
            RaiseErrorOccurred(HtmlParseError.CommentEndedUnexpected);
            StringBuffer.Append(Symbols.Minus).Append(Symbols.Minus).Append(c);
            return null;
        }

        c = Advance();
      }
    }

    /// <summary>
    /// See 8.2.4.51 Comment end bang state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode CommentBangEnd(Char c)
    {
      switch (c)
      {
        case Symbols.Minus:
          StringBuffer.Append(Symbols.Minus).Append(Symbols.Minus).Append(Symbols.ExclamationMark);
          return CommentDashEnd(Advance());
        case Symbols.GreaterThan:
          State = HtmlParseMode.PCData;
          break;
        case Symbols.Null:
          RaiseErrorOccurred(HtmlParseError.Null);
          StringBuffer.Append(Symbols.Minus).Append(Symbols.Minus).Append(Symbols.ExclamationMark).Append(Symbols.Replacement);
          return null;
        case Symbols.EndOfFile:
          RaiseErrorOccurred(HtmlParseError.EOF);
          Back();
          break;
        default:
          StringBuffer.Append(Symbols.Minus).Append(Symbols.Minus).Append(Symbols.ExclamationMark).Append(c);
          return null;
      }

      return NewComment();
    }

    #endregion

    #region Doctype

    /// <summary>
    /// See 8.2.4.52 DOCTYPE state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode Doctype(Char c)
    {
      if (c.IsSpaceCharacter())
      {
        return DoctypeNameBefore(Advance());
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        Back();
        return NewDoctype(true);
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypeUnexpected);
        return DoctypeNameBefore(c);
      }
    }

    /// <summary>
    /// See 8.2.4.53 Before DOCTYPE name state
    /// </summary>
    /// <param name="c">The next input character.</param>
    private HtmlNode DoctypeNameBefore(Char c)
    {
      while (c.IsSpaceCharacter())
        c = Advance();

      if (c.IsUppercaseAscii())
      {
        var doctype = NewDoctype(false);
        StringBuffer.Append(Char.ToLowerInvariant(c));
        return DoctypeName(doctype);
      }
      else if (c == Symbols.Null)
      {
        var doctype = NewDoctype(false);
        AppendReplacement();
        return DoctypeName(doctype);
      }
      else if (c == Symbols.GreaterThan)
      {
        var doctype = NewDoctype(true);
        State = HtmlParseMode.PCData;
        RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
        return doctype;
      }
      else if (c == Symbols.EndOfFile)
      {
        var doctype = NewDoctype(true);
        RaiseErrorOccurred(HtmlParseError.EOF);
        Back();
        return doctype;
      }
      else
      {
        var doctype = NewDoctype(false);
        StringBuffer.Append(c);
        return DoctypeName(doctype);
      }
    }

    /// <summary>
    /// See 8.2.4.54 DOCTYPE name state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeName(HtmlDoctypeNode doctype)
    {
      while (true)
      {
        var c = Advance();

        if (c.IsSpaceCharacter())
        {
          doctype.Value = FlushBuffer();
          return DoctypeNameAfter(doctype);
        }
        else if (c == Symbols.GreaterThan)
        {
          State = HtmlParseMode.PCData;
          doctype.Value = FlushBuffer();
          break;
        }
        else if (c.IsUppercaseAscii())
        {
          StringBuffer.Append(Char.ToLowerInvariant(c));
        }
        else if (c == Symbols.Null)
        {
          AppendReplacement();
        }
        else if (c == Symbols.EndOfFile)
        {
          RaiseErrorOccurred(HtmlParseError.EOF);
          Back();
          doctype.IsQuirksForced = true;
          doctype.Value = FlushBuffer();
          break;
        }
        else
        {
          StringBuffer.Append(c);
        }
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.55 After DOCTYPE name state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeNameAfter(HtmlDoctypeNode doctype)
    {
      var c = SkipSpaces();

      if (c == Symbols.GreaterThan)
      {
        State = HtmlParseMode.PCData;
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        Back();
        doctype.IsQuirksForced = true;
      }
      else if (ContinuesWithInsensitive(Keywords.Public))
      {
        Advance(5);
        return DoctypePublic(doctype);
      }
      else if (ContinuesWithInsensitive(Keywords.System))
      {
        Advance(5);
        return DoctypeSystem(doctype);
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypeUnexpectedAfterName);
        doctype.IsQuirksForced = true;
        return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.56 After DOCTYPE public keyword state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypePublic(HtmlDoctypeNode doctype)
    {
      var c = Advance();

      if (c.IsSpaceCharacter())
      {
        return DoctypePublicIdentifierBefore(doctype);
      }
      else if (c == Symbols.DoubleQuote)
      {
        RaiseErrorOccurred(HtmlParseError.DoubleQuotationMarkUnexpected);
        doctype.PublicIdentifier = String.Empty;
        return DoctypePublicIdentifierDoubleQuoted(doctype);
      }
      else if (c == Symbols.SingleQuote)
      {
        RaiseErrorOccurred(HtmlParseError.SingleQuotationMarkUnexpected);
        doctype.PublicIdentifier = String.Empty;
        return DoctypePublicIdentifierSingleQuoted(doctype);
      }
      else if (c == Symbols.GreaterThan)
      {
        State = HtmlParseMode.PCData;
        RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
        doctype.IsQuirksForced = true;
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        doctype.IsQuirksForced = true;
        Back();
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypePublicInvalid);
        doctype.IsQuirksForced = true;
        return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.57 Before DOCTYPE public identifier state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypePublicIdentifierBefore(HtmlDoctypeNode doctype)
    {
      var c = SkipSpaces();

      if (c == Symbols.DoubleQuote)
      {
        doctype.PublicIdentifier = String.Empty;
        return DoctypePublicIdentifierDoubleQuoted(doctype);
      }
      else if (c == Symbols.SingleQuote)
      {
        doctype.PublicIdentifier = String.Empty;
        return DoctypePublicIdentifierSingleQuoted(doctype);
      }
      else if (c == Symbols.GreaterThan)
      {
        State = HtmlParseMode.PCData;
        RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
        doctype.IsQuirksForced = true;
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        doctype.IsQuirksForced = true;
        Back();
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypePublicInvalid);
        doctype.IsQuirksForced = true;
        return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.58 DOCTYPE public identifier (double-quoted) state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypePublicIdentifierDoubleQuoted(HtmlDoctypeNode doctype)
    {
      while (true)
      {
        var c = Advance();

        if (c == Symbols.DoubleQuote)
        {
          doctype.PublicIdentifier = FlushBuffer();
          return DoctypePublicIdentifierAfter(doctype);
        }
        else if (c == Symbols.Null)
        {
          AppendReplacement();
        }
        else if (c == Symbols.GreaterThan)
        {
          State = HtmlParseMode.PCData;
          RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
          doctype.IsQuirksForced = true;
          doctype.PublicIdentifier = FlushBuffer();
          break;
        }
        else if (c == Symbols.EndOfFile)
        {
          RaiseErrorOccurred(HtmlParseError.EOF);
          Back();
          doctype.IsQuirksForced = true;
          doctype.PublicIdentifier = FlushBuffer();
          break;
        }
        else
        {
          StringBuffer.Append(c);
        }
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.59 DOCTYPE public identifier (single-quoted) state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypePublicIdentifierSingleQuoted(HtmlDoctypeNode doctype)
    {
      while (true)
      {
        var c = Advance();

        if (c == Symbols.SingleQuote)
        {
          doctype.PublicIdentifier = FlushBuffer();
          return DoctypePublicIdentifierAfter(doctype);
        }
        else if (c == Symbols.Null)
        {
          AppendReplacement();
        }
        else if (c == Symbols.GreaterThan)
        {
          State = HtmlParseMode.PCData;
          RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
          doctype.IsQuirksForced = true;
          doctype.PublicIdentifier = FlushBuffer();
          break;
        }
        else if (c == Symbols.EndOfFile)
        {
          RaiseErrorOccurred(HtmlParseError.EOF);
          doctype.IsQuirksForced = true;
          doctype.PublicIdentifier = FlushBuffer();
          Back();
          break;
        }
        else
        {
          StringBuffer.Append(c);
        }
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.60 After DOCTYPE public identifier state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypePublicIdentifierAfter(HtmlDoctypeNode doctype)
    {
      var c = Advance();

      if (c.IsSpaceCharacter())
      {
        return DoctypeBetween(doctype);
      }
      else if (c == Symbols.GreaterThan)
      {
        State = HtmlParseMode.PCData;
      }
      else if (c == Symbols.DoubleQuote)
      {
        RaiseErrorOccurred(HtmlParseError.DoubleQuotationMarkUnexpected);
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierDoubleQuoted(doctype);
      }
      else if (c == Symbols.SingleQuote)
      {
        RaiseErrorOccurred(HtmlParseError.SingleQuotationMarkUnexpected);
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierSingleQuoted(doctype);
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        doctype.IsQuirksForced = true;
        Back();
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypeInvalidCharacter);
        doctype.IsQuirksForced = true;
        return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.61 Between DOCTYPE public and system identifiers state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeBetween(HtmlDoctypeNode doctype)
    {
      var c = SkipSpaces();

      if (c == Symbols.GreaterThan)
      {
        State = HtmlParseMode.PCData;
      }
      else if (c == Symbols.DoubleQuote)
      {
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierDoubleQuoted(doctype);
      }
      else if (c == Symbols.SingleQuote)
      {
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierSingleQuoted(doctype);
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        doctype.IsQuirksForced = true;
        Back();
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypeInvalidCharacter);
        doctype.IsQuirksForced = true;
        return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.62 After DOCTYPE system keyword state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeSystem(HtmlDoctypeNode doctype)
    {
      var c = Advance();

      if (c.IsSpaceCharacter())
      {
        State = HtmlParseMode.PCData;
        return DoctypeSystemIdentifierBefore(doctype);
      }
      else if (c == Symbols.DoubleQuote)
      {
        RaiseErrorOccurred(HtmlParseError.DoubleQuotationMarkUnexpected);
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierDoubleQuoted(doctype);
      }
      else if (c == Symbols.SingleQuote)
      {
        RaiseErrorOccurred(HtmlParseError.SingleQuotationMarkUnexpected);
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierSingleQuoted(doctype);
      }
      else if (c == Symbols.GreaterThan)
      {
        RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
        doctype.SystemIdentifier = FlushBuffer();
        doctype.IsQuirksForced = true;
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        doctype.IsQuirksForced = true;
        Back();
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypeSystemInvalid);
        doctype.IsQuirksForced = true;
        return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.63 Before DOCTYPE system identifier state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeSystemIdentifierBefore(HtmlDoctypeNode doctype)
    {
      var c = SkipSpaces();

      if (c == Symbols.DoubleQuote)
      {
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierDoubleQuoted(doctype);
      }
      else if (c == Symbols.SingleQuote)
      {
        doctype.SystemIdentifier = String.Empty;
        return DoctypeSystemIdentifierSingleQuoted(doctype);
      }
      else if (c == Symbols.GreaterThan)
      {
        State = HtmlParseMode.PCData;
        RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
        doctype.IsQuirksForced = true;
        doctype.SystemIdentifier = FlushBuffer();
      }
      else if (c == Symbols.EndOfFile)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
        doctype.IsQuirksForced = true;
        doctype.SystemIdentifier = FlushBuffer();
        Back();
      }
      else
      {
        RaiseErrorOccurred(HtmlParseError.DoctypeInvalidCharacter);
        doctype.IsQuirksForced = true;
        return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.64 DOCTYPE system identifier (double-quoted) state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeSystemIdentifierDoubleQuoted(HtmlDoctypeNode doctype)
    {
      while (true)
      {
        var c = Advance();

        if (c == Symbols.DoubleQuote)
        {
          doctype.SystemIdentifier = FlushBuffer();
          return DoctypeSystemIdentifierAfter(doctype);
        }
        else if (c == Symbols.Null)
        {
          AppendReplacement();
        }
        else if (c == Symbols.GreaterThan)
        {
          State = HtmlParseMode.PCData;
          RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
          doctype.IsQuirksForced = true;
          doctype.SystemIdentifier = FlushBuffer();
          break;
        }
        else if (c == Symbols.EndOfFile)
        {
          RaiseErrorOccurred(HtmlParseError.EOF);
          doctype.IsQuirksForced = true;
          doctype.SystemIdentifier = FlushBuffer();
          Back();
          break;
        }
        else
        {
          StringBuffer.Append(c);
        }
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.65 DOCTYPE system identifier (single-quoted) state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeSystemIdentifierSingleQuoted(HtmlDoctypeNode doctype)
    {
      while (true)
      {
        var c = Advance();

        switch (c)
        {
          case Symbols.SingleQuote:
            doctype.SystemIdentifier = FlushBuffer();
            return DoctypeSystemIdentifierAfter(doctype);
          case Symbols.Null:
            AppendReplacement();
            continue;
          case Symbols.GreaterThan:
            State = HtmlParseMode.PCData;
            RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
            doctype.IsQuirksForced = true;
            doctype.SystemIdentifier = FlushBuffer();
            break;
          case Symbols.EndOfFile:
            RaiseErrorOccurred(HtmlParseError.EOF);
            doctype.IsQuirksForced = true;
            doctype.SystemIdentifier = FlushBuffer();
            Back();
            break;
          default:
            StringBuffer.Append(c);
            continue;
        }

        return doctype;
      }
    }

    /// <summary>
    /// See 8.2.4.66 After DOCTYPE system identifier state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode DoctypeSystemIdentifierAfter(HtmlDoctypeNode doctype)
    {
      var c = SkipSpaces();

      switch (c)
      {
        case Symbols.GreaterThan:
          State = HtmlParseMode.PCData;
          break;
        case Symbols.EndOfFile:
          RaiseErrorOccurred(HtmlParseError.EOF);
          doctype.IsQuirksForced = true;
          Back();
          break;
        default:
          RaiseErrorOccurred(HtmlParseError.DoctypeInvalidCharacter);
          return BogusDoctype(doctype);
      }

      return doctype;
    }

    /// <summary>
    /// See 8.2.4.67 Bogus DOCTYPE state
    /// </summary>
    /// <param name="doctype">The current doctype token.</param>
    private HtmlNode BogusDoctype(HtmlDoctypeNode doctype)
    {
      while (true)
      {
        switch (Advance())
        {
          case Symbols.GreaterThan:
            State = HtmlParseMode.PCData;
            break;
          case Symbols.EndOfFile:
            Back();
            break;
          default:
            continue;
        }

        return doctype;
      }
    }

    #endregion

    #region Attributes

    private enum AttributeState : byte
    {
      BeforeName,
      Name,
      AfterName,
      BeforeValue,
      QuotedValue,
      AfterValue,
      UnquotedValue
    }

    private HtmlNode ParseAttributes(HtmlTagNode tag)
    {
      var state = AttributeState.BeforeName;
      var quote = Symbols.DoubleQuote;
      var c = Symbols.Null;

      while (true)
      {
        switch (state)
        {
          // See 8.2.4.34 Before attribute name state
          case AttributeState.BeforeName:
            {
              c = SkipSpaces();

              if (c == Symbols.Solidus)
              {
                return TagSelfClosing(tag);
              }
              else if (c == Symbols.GreaterThan)
              {
                return EmitTag(tag);
              }
              else if (c.IsUppercaseAscii())
              {
                StringBuffer.Append(Char.ToLowerInvariant(c));
                state = AttributeState.Name;
              }
              else if (c == Symbols.Null)
              {
                AppendReplacement();
                state = AttributeState.Name;
              }
              else if (c == Symbols.SingleQuote || c == Symbols.DoubleQuote || c == Symbols.Equality || c == Symbols.LessThan)
              {
                RaiseErrorOccurred(HtmlParseError.AttributeNameInvalid);
                StringBuffer.Append(c);
                state = AttributeState.Name;
              }
              else if (c != Symbols.EndOfFile)
              {
                StringBuffer.Append(c);
                state = AttributeState.Name;
              }
              else
              {
                return NewEof();
              }

              break;
            }

          // See 8.2.4.35 Attribute name state
          case AttributeState.Name:
            {
              c = Advance();

              if (c == Symbols.Equality)
              {
                tag.AddAttribute(FlushBuffer());
                state = AttributeState.BeforeValue;
              }
              else if (c == Symbols.GreaterThan)
              {
                tag.AddAttribute(FlushBuffer());
                return EmitTag(tag);
              }
              else if (c.IsSpaceCharacter())
              {
                tag.AddAttribute(FlushBuffer());
                state = AttributeState.AfterName;
              }
              else if (c == Symbols.Solidus)
              {
                tag.AddAttribute(FlushBuffer());
                return TagSelfClosing(tag);
              }
              else if (c.IsUppercaseAscii())
              {
                StringBuffer.Append(Char.ToLowerInvariant(c));
              }
              else if (c == Symbols.DoubleQuote || c == Symbols.SingleQuote || c == Symbols.LessThan)
              {
                RaiseErrorOccurred(HtmlParseError.AttributeNameInvalid);
                StringBuffer.Append(c);
              }
              else if (c == Symbols.Null)
              {
                AppendReplacement();
              }
              else if (c != Symbols.EndOfFile)
              {
                StringBuffer.Append(c);
              }
              else
              {
                return NewEof();
              }

              break;
            }

          // See 8.2.4.36 After attribute name state
          case AttributeState.AfterName:
            {
              c = SkipSpaces();

              if (c == Symbols.GreaterThan)
              {
                return EmitTag(tag);
              }
              else if (c == Symbols.Equality)
              {
                state = AttributeState.BeforeValue;
              }
              else if (c == Symbols.Solidus)
              {
                return TagSelfClosing(tag);
              }
              else if (c.IsUppercaseAscii())
              {
                StringBuffer.Append(Char.ToLowerInvariant(c));
                state = AttributeState.Name;
              }
              else if (c == Symbols.DoubleQuote || c == Symbols.SingleQuote || c == Symbols.LessThan)
              {
                RaiseErrorOccurred(HtmlParseError.AttributeNameInvalid);
                StringBuffer.Append(c);
                state = AttributeState.Name;
              }
              else if (c == Symbols.Null)
              {
                AppendReplacement();
                state = AttributeState.Name;
              }
              else if (c != Symbols.EndOfFile)
              {
                StringBuffer.Append(c);
                state = AttributeState.Name;
              }
              else
              {
                return NewEof();
              }

              break;
            }

          // See 8.2.4.37 Before attribute value state
          case AttributeState.BeforeValue:
            {
              c = SkipSpaces();

              if (c == Symbols.DoubleQuote || c == Symbols.SingleQuote)
              {
                state = AttributeState.QuotedValue;
                quote = c;
              }
              else if (c == Symbols.Ampersand)
              {
                state = AttributeState.UnquotedValue;
              }
              else if (c == Symbols.GreaterThan)
              {
                RaiseErrorOccurred(HtmlParseError.TagClosedWrong);
                return EmitTag(tag);
              }
              else if (c == Symbols.LessThan || c == Symbols.Equality || c == Symbols.CurvedQuote)
              {
                RaiseErrorOccurred(HtmlParseError.AttributeValueInvalid);
                StringBuffer.Append(c);
                state = AttributeState.UnquotedValue;
                c = Advance();
              }
              else if (c == Symbols.Null)
              {
                AppendReplacement();
                state = AttributeState.UnquotedValue;
                c = Advance();
              }
              else if (c != Symbols.EndOfFile)
              {
                StringBuffer.Append(c);
                state = AttributeState.UnquotedValue;
                c = Advance();
              }
              else
              {
                return NewEof();
              }

              break;
            }

          // See 8.2.4.38 Attribute value (double-quoted) state
          // and 8.2.4.39 Attribute value (single-quoted) state
          case AttributeState.QuotedValue:
            {
              c = Advance();

              if (c == quote)
              {
                tag.SetAttributeValue(FlushBuffer());
                state = AttributeState.AfterValue;
              }
              else if (c == Symbols.Ampersand)
              {
                AppendCharacterReference(Advance(), quote);
              }
              else if (c == Symbols.Null)
              {
                AppendReplacement();
              }
              else if (c != Symbols.EndOfFile)
              {
                StringBuffer.Append(c);
              }
              else
              {
                return NewEof();
              }

              break;
            }

          // See 8.2.4.40 Attribute value (unquoted) state
          case AttributeState.UnquotedValue:
            {
              if (c == Symbols.GreaterThan)
              {
                tag.SetAttributeValue(FlushBuffer());
                return EmitTag(tag);
              }
              else if (c.IsSpaceCharacter())
              {
                tag.SetAttributeValue(FlushBuffer());
                state = AttributeState.BeforeName;
              }
              else if (c == Symbols.Ampersand)
              {
                AppendCharacterReference(Advance(), Symbols.GreaterThan);
                c = Advance();
              }
              else if (c == Symbols.Null)
              {
                AppendReplacement();
                c = Advance();
              }
              else if (c == Symbols.DoubleQuote || c == Symbols.SingleQuote || c == Symbols.LessThan || c == Symbols.Equality || c == Symbols.CurvedQuote)
              {
                RaiseErrorOccurred(HtmlParseError.AttributeValueInvalid);
                StringBuffer.Append(c);
                c = Advance();
              }
              else if (c != Symbols.EndOfFile)
              {
                StringBuffer.Append(c);
                c = Advance();
              }
              else
              {
                return NewEof();
              }

              break;
            }

          // See 8.2.4.42 After attribute value (quoted) state
          case AttributeState.AfterValue:
            {
              c = Advance();

              if (c == Symbols.GreaterThan)
              {
                return EmitTag(tag);
              }
              else if (c.IsSpaceCharacter())
              {
                state = AttributeState.BeforeName;
              }
              else if (c == Symbols.Solidus)
              {
                return TagSelfClosing(tag);
              }
              else if (c == Symbols.EndOfFile)
              {
                return NewEof();
              }
              else
              {
                RaiseErrorOccurred(HtmlParseError.AttributeNameExpected);
                Back();
                state = AttributeState.BeforeName;
              }

              break;
            }
        }
      }
    }

    #endregion

    #region Script

    private enum ScriptState : byte
    {
      Normal,
      OpenTag,
      EndTag,
      StartEscape,
      Escaped,
      StartEscapeDash,
      EscapedDash,
      EscapedDashDash,
      EscapedOpenTag,
      EscapedEndTag,
      EscapedNameEndTag,
      StartDoubleEscape,
      EscapedDouble,
      EscapedDoubleDash,
      EscapedDoubleDashDash,
      EscapedDoubleOpenTag,
      EndDoubleEscape
    }

    private HtmlNode ScriptData(Char c)
    {
      var length = _lastStartTag.Length;
      var scriptLength = TagNames.Script.Length;
      var state = ScriptState.Normal;
      var offset = 0;

      while (true)
      {
        switch (state)
        {
          // See 8.2.4.6 Script data state
          case ScriptState.Normal:
            {
              switch (c)
              {
                case Symbols.Null:
                  AppendReplacement();
                  break;

                case Symbols.LessThan:
                  StringBuffer.Append(Symbols.LessThan);
                  state = ScriptState.OpenTag;
                  continue;

                case Symbols.EndOfFile:
                  Back();
                  return NewCharacter();

                default:
                  StringBuffer.Append(c);
                  break;
              }

              c = Advance();
              break;
            }

          // See 8.2.4.17 Script data less-than sign state
          case ScriptState.OpenTag:
            {
              c = Advance();

              if (c == Symbols.Solidus)
              {
                state = ScriptState.EndTag;
              }
              else if (c == Symbols.ExclamationMark)
              {
                state = ScriptState.StartEscape;
              }
              else
              {
                state = ScriptState.Normal;
              }

              break;
            }

          // See 8.2.4.20 Script data escape start state
          case ScriptState.StartEscape:
            {
              StringBuffer.Append(Symbols.ExclamationMark);
              c = Advance();

              if (c == Symbols.Minus)
              {
                state = ScriptState.StartEscapeDash;
              }
              else
              {
                state = ScriptState.Normal;
              }

              break;
            }

          // See 8.2.4.21 Script data escape start dash state
          case ScriptState.StartEscapeDash:
            {
              c = Advance();
              StringBuffer.Append(Symbols.Minus);

              if (c == Symbols.Minus)
              {
                StringBuffer.Append(Symbols.Minus);
                state = ScriptState.EscapedDashDash;
              }
              else
              {
                state = ScriptState.Normal;
              }

              break;
            }

          // See 8.2.4.18 Script data end tag open state
          case ScriptState.EndTag:
            {
              c = Advance();
              offset = StringBuffer.Append(Symbols.Solidus).Length;
              var tag = NewTagClose();

              while (c.IsLetter())
              {
                // See 8.2.4.19 Script data end tag name state
                StringBuffer.Append(c);
                c = Advance();
                var isspace = c.IsSpaceCharacter();
                var isclosed = c == Symbols.GreaterThan;
                var isslash = c == Symbols.Solidus;
                var hasLength = StringBuffer.Length - offset == length;

                if (hasLength && (isspace || isclosed || isslash))
                {
                  var name = StringBuffer.ToString(offset, length);

                  if (name.Isi(_lastStartTag))
                  {
                    if (offset > 2)
                    {
                      Back(3 + length);
                      StringBuffer.Remove(offset - 2, length + 2);
                      return NewCharacter();
                    }

                    StringBuffer.Clear();

                    if (isspace)
                    {
                      tag.Value = _lastStartTag;
                      return ParseAttributes(tag);
                    }
                    else if (isslash)
                    {
                      tag.Value = _lastStartTag;
                      return TagSelfClosing(tag);
                    }
                    else if (isclosed)
                    {
                      tag.Value = _lastStartTag;
                      return EmitTag(tag);
                    }
                  }
                }
              }

              state = ScriptState.Normal;
              break;
            }

          // See 8.2.4.22 Script data escaped state
          case ScriptState.Escaped:
            {
              switch (c)
              {
                case Symbols.Minus:
                  StringBuffer.Append(Symbols.Minus);
                  c = Advance();
                  state = ScriptState.EscapedDash;
                  continue;
                case Symbols.LessThan:
                  c = Advance();
                  state = ScriptState.EscapedOpenTag;
                  continue;
                case Symbols.Null:
                  AppendReplacement();
                  break;
                case Symbols.EndOfFile:
                  Back();
                  return NewCharacter();
                default:
                  state = ScriptState.Normal;
                  continue;
              }

              c = Advance();
              break;
            }

          // See 8.2.4.23 Script data escaped dash state
          case ScriptState.EscapedDash:
            {
              switch (c)
              {
                case Symbols.Minus:
                  StringBuffer.Append(Symbols.Minus);
                  state = ScriptState.EscapedDashDash;
                  continue;
                case Symbols.LessThan:
                  c = Advance();
                  state = ScriptState.EscapedOpenTag;
                  continue;
                case Symbols.Null:
                  AppendReplacement();
                  break;
                case Symbols.EndOfFile:
                  Back();
                  return NewCharacter();
                default:
                  StringBuffer.Append(c);
                  break;
              }

              c = Advance();
              state = ScriptState.Escaped;
              break;
            }

          // See 8.2.4.24 Script data escaped dash dash state
          case ScriptState.EscapedDashDash:
            {
              c = Advance();

              switch (c)
              {
                case Symbols.Minus:
                  StringBuffer.Append(Symbols.Minus);
                  break;
                case Symbols.LessThan:
                  c = Advance();
                  state = ScriptState.EscapedOpenTag;
                  continue;
                case Symbols.GreaterThan:
                  StringBuffer.Append(Symbols.GreaterThan);
                  c = Advance();
                  state = ScriptState.Normal;
                  continue;
                case Symbols.Null:
                  AppendReplacement();
                  c = Advance();
                  state = ScriptState.Escaped;
                  continue;
                case Symbols.EndOfFile:
                  return NewCharacter();
                default:
                  StringBuffer.Append(c);
                  c = Advance();
                  state = ScriptState.Escaped;
                  continue;
              }

              break;
            }

          // See 8.2.4.25 Script data escaped less-than sign state
          case ScriptState.EscapedOpenTag:
            {
              if (c == Symbols.Solidus)
              {
                c = Advance();
                state = ScriptState.EscapedEndTag;
              }
              else if (c.IsLetter())
              {
                offset = StringBuffer.Append(Symbols.LessThan).Length;
                StringBuffer.Append(c);
                state = ScriptState.StartDoubleEscape;
              }
              else
              {
                StringBuffer.Append(Symbols.LessThan);
                state = ScriptState.Escaped;
              }

              break;
            }

          // See 8.2.4.26 Script data escaped end tag open state
          case ScriptState.EscapedEndTag:
            {
              offset = StringBuffer.Append(Symbols.LessThan).Append(Symbols.Solidus).Length;

              if (c.IsLetter())
              {
                StringBuffer.Append(c);
                state = ScriptState.EscapedNameEndTag;
              }
              else
              {
                state = ScriptState.Escaped;
              }

              break;
            }

          // See 8.2.4.27 Script data escaped end tag name state
          case ScriptState.EscapedNameEndTag:
            {
              c = Advance();
              var hasLength = StringBuffer.Length - offset == scriptLength;

              if (hasLength && (c == Symbols.Solidus || c == Symbols.GreaterThan || c.IsSpaceCharacter()) &&
                  StringBuffer.ToString(offset, scriptLength).Isi(TagNames.Script))
              {
                Back(scriptLength + 3);
                StringBuffer.Remove(offset - 2, scriptLength + 2);
                return NewCharacter();
              }
              else if (!c.IsLetter())
              {
                state = ScriptState.Escaped;
              }
              else
              {
                StringBuffer.Append(c);
              }

              break;
            }

          // See 8.2.4.28 Script data double escape start state
          case ScriptState.StartDoubleEscape:
            {
              c = Advance();
              var hasLength = StringBuffer.Length - offset == scriptLength;

              if (hasLength && (c == Symbols.Solidus || c == Symbols.GreaterThan || c.IsSpaceCharacter()))
              {
                var isscript = StringBuffer.ToString(offset, scriptLength).Isi(TagNames.Script);
                StringBuffer.Append(c);
                c = Advance();
                state = isscript ? ScriptState.EscapedDouble : ScriptState.Escaped;
              }
              else if (c.IsLetter())
              {
                StringBuffer.Append(c);
              }
              else
              {
                state = ScriptState.Escaped;
              }

              break;
            }

          // See 8.2.4.29 Script data double escaped state
          case ScriptState.EscapedDouble:
            {
              switch (c)
              {
                case Symbols.Minus:
                  StringBuffer.Append(Symbols.Minus);
                  c = Advance();
                  state = ScriptState.EscapedDoubleDash;
                  continue;

                case Symbols.LessThan:
                  StringBuffer.Append(Symbols.LessThan);
                  c = Advance();
                  state = ScriptState.EscapedDoubleOpenTag;
                  continue;

                case Symbols.Null:
                  AppendReplacement();
                  break;

                case Symbols.EndOfFile:
                  RaiseErrorOccurred(HtmlParseError.EOF);
                  Back();
                  return NewCharacter();
              }

              StringBuffer.Append(c);
              c = Advance();
              break;
            }

          // See 8.2.4.30 Script data double escaped dash state
          case ScriptState.EscapedDoubleDash:
            {
              switch (c)
              {
                case Symbols.Minus:
                  StringBuffer.Append(Symbols.Minus);
                  state = ScriptState.EscapedDoubleDashDash;
                  continue;

                case Symbols.LessThan:
                  StringBuffer.Append(Symbols.LessThan);
                  c = Advance();
                  state = ScriptState.EscapedDoubleOpenTag;
                  continue;

                case Symbols.Null:
                  RaiseErrorOccurred(HtmlParseError.Null);
                  c = Symbols.Replacement;
                  break;

                case Symbols.EndOfFile:
                  RaiseErrorOccurred(HtmlParseError.EOF);
                  Back();
                  return NewCharacter();
              }

              state = ScriptState.EscapedDouble;
              break;
            }

          // See 8.2.4.31 Script data double escaped dash dash state
          case ScriptState.EscapedDoubleDashDash:
            {
              c = Advance();

              switch (c)
              {
                case Symbols.Minus:
                  StringBuffer.Append(Symbols.Minus);
                  break;

                case Symbols.LessThan:
                  StringBuffer.Append(Symbols.LessThan);
                  c = Advance();
                  state = ScriptState.EscapedDoubleOpenTag;
                  continue;

                case Symbols.GreaterThan:
                  StringBuffer.Append(Symbols.GreaterThan);
                  c = Advance();
                  state = ScriptState.Normal;
                  continue;

                case Symbols.Null:
                  AppendReplacement();
                  c = Advance();
                  state = ScriptState.EscapedDouble;
                  continue;

                case Symbols.EndOfFile:
                  RaiseErrorOccurred(HtmlParseError.EOF);
                  Back();
                  return NewCharacter();

                default:
                  StringBuffer.Append(c);
                  c = Advance();
                  state = ScriptState.EscapedDouble;
                  continue;
              }

              break;
            }

          // See 8.2.4.32 Script data double escaped less-than sign state
          case ScriptState.EscapedDoubleOpenTag:
            {
              if (c == Symbols.Solidus)
              {
                offset = StringBuffer.Append(Symbols.Solidus).Length;
                state = ScriptState.EndDoubleEscape;
              }
              else
              {
                state = ScriptState.EscapedDouble;
              }

              break;
            }

          // See 8.2.4.33 Script data double escape end state
          case ScriptState.EndDoubleEscape:
            {
              c = Advance();
              var hasLength = StringBuffer.Length - offset == scriptLength;

              if (hasLength && (c.IsSpaceCharacter() || c == Symbols.Solidus || c == Symbols.GreaterThan))
              {
                var isscript = StringBuffer.ToString(offset, scriptLength).Isi(TagNames.Script);
                StringBuffer.Append(c);
                c = Advance();
                state = isscript ? ScriptState.Escaped : ScriptState.EscapedDouble;
              }
              else if (c.IsLetter())
              {
                StringBuffer.Append(c);
              }
              else
              {
                state = ScriptState.EscapedDouble;
              }

              break;
            }
        }
      }
    }

    #endregion

    #region Tokens

    private HtmlNode NewCharacter()
    {
      var content = FlushBuffer();
      return new HtmlNode(HtmlTokenType.Text, _position, content);
    }

    private HtmlCommentNode NewComment()
    {
      var content = FlushBuffer();
      return new HtmlCommentNode(_position, content);
    }

    private HtmlNode NewEof(Boolean acceptable = false)
    {
      if (!acceptable)
      {
        RaiseErrorOccurred(HtmlParseError.EOF);
      }

      return new HtmlNode(HtmlTokenType.EndOfFile, _position);
    }

    private HtmlDoctypeNode NewDoctype(Boolean quirksForced)
    {
      return new HtmlDoctypeNode(quirksForced, _position);
    }

    private HtmlTagNode NewTagOpen()
    {
      return new HtmlTagNode(HtmlTokenType.StartTag, _position);
    }

    private HtmlTagNode NewTagClose()
    {
      return new HtmlTagNode(HtmlTokenType.EndTag, _position);
    }

    #endregion

    #region Helpers

    private void RaiseErrorOccurred(HtmlParseError code)
    {
      RaiseErrorOccurred(code, GetCurrentPosition());
    }

    private void AppendReplacement()
    {
      RaiseErrorOccurred(HtmlParseError.Null);
      StringBuffer.Append(Symbols.Replacement);
    }

    private HtmlNode CreateIfAppropriate(Char c)
    {
      var isspace = c.IsSpaceCharacter();
      var isclosed = c == Symbols.GreaterThan;
      var isslash = c == Symbols.Solidus;
      var hasLength = StringBuffer.Length == _lastStartTag.Length;

      if (hasLength && (isspace || isclosed || isslash) && StringBuffer.ToString().Is(_lastStartTag))
      {
        var tag = NewTagClose();
        StringBuffer.Clear();

        if (isspace)
        {
          tag.Value = _lastStartTag;
          return ParseAttributes(tag);
        }
        else if (isslash)
        {
          tag.Value = _lastStartTag;
          return TagSelfClosing(tag);
        }
        else if (isclosed)
        {
          tag.Value = _lastStartTag;
          return EmitTag(tag);
        }
      }

      return null;
    }

    private HtmlNode EmitTag(HtmlTagNode tag)
    {
      var attributes = tag.Attributes;
      State = HtmlParseMode.PCData;

      switch (tag.Type)
      {
        case HtmlTokenType.StartTag:
          for (var i = attributes.Count - 1; i > 0; i--)
          {
            for (var j = i - 1; j >= 0; j--)
            {
              if (attributes[j].Key == attributes[i].Key)
              {
                attributes.RemoveAt(i);
                RaiseErrorOccurred(HtmlParseError.AttributeDuplicateOmitted, tag.Position);
                break;
              }
            }
          }

          if (tag.Value.Is(TagNames.Script))
            State = HtmlParseMode.Script;

          _lastStartTag = tag.Value;
          break;
        case HtmlTokenType.EndTag:
          if (tag.IsSelfClosing)
          {
            RaiseErrorOccurred(HtmlParseError.EndTagCannotBeSelfClosed, tag.Position);
          }

          if (attributes.Count != 0)
          {
            RaiseErrorOccurred(HtmlParseError.EndTagCannotHaveAttributes, tag.Position);
          }

          break;
      }

      return tag;
    }

    public IEnumerator<HtmlNode> GetEnumerator()
    {
      return this;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
      return this.GetEnumerator();
    }

    bool IEnumerator.MoveNext()
    {
      return Read();
    }

    void IEnumerator.Reset()
    {
      throw new NotSupportedException();
    }

    bool IXmlLineInfo.HasLineInfo()
    {
      return true;
    }

    #endregion
  }
}
