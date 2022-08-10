#nullable enable

using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.IL
{
	partial class ExpressionTreeCast
	{
		public bool IsChecked { get; set; }

		public ExpressionTreeCast(IType type, ILInstruction argument, bool isChecked)
			: base(OpCode.ExpressionTreeCast, argument)
		{
			this.type = type;
			this.IsChecked = isChecked;
		}

		public override void WriteTo(IDecompilerOutput output, ILAstWritingOptions options)
		{
			WriteILRange(output, options);
			output.Write(OpCode);
			if (IsChecked)
				output.Write(".checked", BoxedTextColor.OpCode);
			output.Write(" ", BoxedTextColor.Text);
			type.WriteTo(output);
			var braceInfo = OpenBrace(output, "(");
			Argument.WriteTo(output, options);
			CloseBrace(output, braceInfo, ")", CodeBracesRangeFlags.Parentheses);
		}
	}
}
