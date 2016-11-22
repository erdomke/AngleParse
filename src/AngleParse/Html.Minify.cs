﻿using AngleParse.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AngleParse
{
  public static partial class Html
  {
    public static string Minify(TextSource html, HtmlMinifySettings settings = null)
    {
      var sb = new StringBuilder(html.Length);
      using (var reader = new HtmlReader(html))
      using (var sw = new StringWriter(sb))
      {
        reader.Minify(settings).ToHtml(sw, new HtmlWriterSettings());
        return sw.ToString();
      }
    }

    public static void Minify(TextSource html, HtmlTextWriter writer, HtmlMinifySettings settings = null)
    {
      using (var reader = new HtmlReader(html))
      {
        reader.Minify(settings).ToHtml(writer);
      }
    }

    private enum MinifyState : byte
    {
      Compressed,
      LastCharWasSpace,
      SpaceNeeded
    }
    private enum ContainingTag : byte
    {
      None,
      WhitespacePreserve,
      Script
    }

    public static IEnumerable<HtmlNode> Minify(this IEnumerable<HtmlNode> reader, HtmlMinifySettings settings = null)
    {
      settings = settings ?? HtmlMinifySettings.ReadOnlyDefault;
      var state = MinifyState.LastCharWasSpace;
      var tagState = ContainingTag.None;
      int trimStart;
      int trimEnd;
      StringBuilder builder = null;
      var jsMin = new JSMin();

      foreach (var node in reader)
      {
        if (node.Type == HtmlTokenType.Comment)
        {
          // Ignore comments, unless they are conditional
          if (node.Value.StartsWith("[if") || node.Value.EndsWith("endif]"))
            yield return node;
        }
        else if (node.Type == HtmlTokenType.Text)
        {
          if (node.Value == "" || node.Value == null)
          {
            // do nothing
          }
          else if (tagState == ContainingTag.WhitespacePreserve)
          {
            yield return node;
            state = MinifyState.LastCharWasSpace;
          }
          else if (tagState == ContainingTag.Script)
          {
            if (builder == null)
              builder = Pool.NewStringBuilder();
            builder.Append(node.Value);
          }
          else
          {
            TrimIndices(node.Value, out trimStart, out trimEnd);
            if (trimEnd < 0)
            {
              // Do nothing for an empty or null string
            }
            else if (trimEnd < trimStart)
            {
              if (state == MinifyState.Compressed)
                state = MinifyState.SpaceNeeded;
            }
            else
            {
              if (state == MinifyState.SpaceNeeded && trimStart == 0)
              {
                yield return new HtmlNode(HtmlTokenType.Text, node.Position, " ");
                state = MinifyState.LastCharWasSpace;
              }

              if (state == MinifyState.LastCharWasSpace || trimStart == 0)
              {
                yield return new HtmlNode(HtmlTokenType.Text, node.Position, GetCompressedString(node.Value, trimStart, trimEnd));
              }
              else
              {
                yield return new HtmlNode(HtmlTokenType.Text, node.Position, GetCompressedString(node.Value, trimStart - 1, trimEnd));
              }

              if (trimEnd < node.Value.Length - 1)
                state = MinifyState.SpaceNeeded;
              else
                state = MinifyState.Compressed;
            }
          }
        }
        else
        {
          if (state == MinifyState.SpaceNeeded)
          {
            if (node.Type == HtmlTokenType.EndTag && settings.BlockLevelElements.Contains(node.Value))
            {
              state = MinifyState.LastCharWasSpace;
            }
            else
            {
              yield return new HtmlNode(HtmlTokenType.Text, node.Position, " ");
              if (settings.PreserveSurroundingSpaceTags.Contains(node.Value))
                state = MinifyState.Compressed;
              else
                state = MinifyState.LastCharWasSpace;
            }
          }

          if (node.Type == HtmlTokenType.EndTag && node.Value == "script" && builder != null)
          {
            yield return new HtmlNode(HtmlTokenType.Text, node.Position, jsMin.Minify(new StringBuilderReader(builder)));
            builder.ToPool();
          }

          var tag = node as HtmlTagNode;
          if (tag != null)
          {
            if (tag.Attributes.Any(a => a.Key == "style" || a.Key == "class"))
            {
              var newTag = new HtmlTagNode(tag.Type, tag.Position, tag.Value);
              foreach (var attr in tag.Attributes)
              {
                if (attr.Key == "style")
                  newTag.AddAttribute(attr.Key, TrimStyleString(attr.Value));
                else if (attr.Key == "class")
                  newTag.AddAttribute(attr.Key, GetCompressedString(attr.Value));
                else
                  newTag.Attributes.Add(attr);
              }
              yield return newTag;
            }
            else
            {
              yield return node;
            }
          }
          else
          {
            yield return node;
          }


          if (node.Type == HtmlTokenType.StartTag && settings.PreserveInnerSpaceTags.Contains(node.Value))
            tagState = ContainingTag.WhitespacePreserve;
          else if (node.Type == HtmlTokenType.StartTag && node.Value == "script")
            tagState = ContainingTag.Script;
          else if (node.Type == HtmlTokenType.EndTag && 
            (settings.PreserveInnerSpaceTags.Contains(node.Value) || node.Value == "script"))
            tagState = ContainingTag.None;
        }
      }
    }
    
    private static void TrimIndices(string value, out int start, out int end)
    {
      start = 0;
      end = -1;
      if (value == null)
        return;

      while (start < value.Length && value[start].IsSpaceCharacter()) start++;

      end = value.Length - 1;
      while (end >= start && value[end].IsSpaceCharacter()) end--;
    }
    private static string TrimStyleString(string value)
    {
      var start = 0;
      var end = -1;
      if (value == null || value == "")
        return value;

      while (start < value.Length && value[start].IsSpaceCharacter()) start++;

      end = value.Length - 1;
      while (end >= start && (value[end].IsSpaceCharacter() || value[end] == ';')) end--;

      if (start == 0 && end == value.Length - 1)
        return value;
      else
        return value.Substring(start, end - start + 1);
    }
    private static string GetCompressedString(string value)
    {
      int start;
      int end;
      TrimIndices(value, out start, out end);
      return GetCompressedString(value, start, end);
    }
    private static string GetCompressedString(string value, int start, int end)
    {
      if (end > start)
      {
        char[] buffer = null;
        int b = -1;

        for (var i = start + 1; i <= end; i++)
        {
          if (value[i].IsSpaceCharacter()
            && value[i - 1].IsSpaceCharacter())
          {
            if (buffer == null)
            {
              buffer = new char[value.Length];
              for (var j = start; j < i; j++)
                buffer[j - start] = value[j];
              b = i - start;
            }
          }
          else if (buffer != null)
          {
            buffer[b++] = value[i];
          }
        }

        if (buffer != null)
          return new string(buffer, 0, b);
      }
      if (start == 0 && end == value.Length - 1)
        return value;
      else if (end >= start)
        return value.Substring(start, end - start + 1);
      return string.Empty;
    }
  }
}