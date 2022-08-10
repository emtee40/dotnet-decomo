#nullable enable
// Copyright (c) 2017 Siegfried Pammer
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace ICSharpCode.Decompiler.IL
{
	partial class LockInstruction
	{
		public override bool SafeToAddToEndILSpans {
			get { return false; }
		}

		public override void WriteTo(IDecompilerOutput output, ILAstWritingOptions options)
		{
			WriteILRange(output, options);
			output.Write("lock", BoxedTextColor.Keyword);
			output.Write(" ", BoxedTextColor.Text);
			var braceInfo = OpenBrace(output, "(");
			OnExpression.WriteTo(output, options);
			CloseBrace(output, braceInfo, ")", CodeBracesRangeFlags.Parentheses);
			output.Write(" ", BoxedTextColor.Text);
			braceInfo = OpenBrace(output, "{");
			output.WriteLine();
			output.IncreaseIndent();
			Body.WriteTo(output, options);
			output.DecreaseIndent();
			output.WriteLine();
			CloseBrace(output, braceInfo, "}", CodeBracesRangeFlags.LockBraces);
		}
	}
}
