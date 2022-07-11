namespace ICSharpCode.Decompiler.IL
{
	partial class PinnedRegion : ILInstruction
	{
		public override bool SafeToAddToEndILSpans {
			get { return false; }
		}
	}
}
