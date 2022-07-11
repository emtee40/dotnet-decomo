using System.Collections.Generic;
using System.Diagnostics;

using dnSpy.Contracts.Decompiler;

namespace ICSharpCode.Decompiler.IL
{
	public static class ILSpanUtils
	{
		public static void NopMergeILSpans(Block block, ref int i) {
			var body = block.Instructions;

			var j = i;
			do {
				j++;
			}
			while (j < body.Count && body[j] is Nop nop && nop.Kind == NopKind.Normal);

			var spans = new List<ILSpan>(j - i);
			while (i < j) {
				body[i].AddSelfAndChildrenRecursiveILSpans(spans);
				i++;
			}
			i--;

			ILInstruction prevNode = null, nextNode = null;
			ILInstruction prev = null, next = null;
			if (i - 1 >= 0)
				prev = prevNode = body[i - 1];
			if (i + 1 < body.Count)
				next = nextNode = body[i + 1];

			ILInstruction node = null;

			if (prev != null && !prev.IsPrefixed()) {
				switch (prev.OpCode) {
					case OpCode.Call:
					case OpCode.CallIndirect:
					case OpCode.CallVirt:
						node = prev;
						break;
				}
			}

			if (next != null && !next.IsPrefixed()) {
				if (next is Leave leave && !leave.IsLeavingFunction)
					node = next;
			}

			if (node != null && node == prevNode) {
				if (prevNode != null && prevNode.SafeToAddToEndILSpans)
					prevNode.EndILSpans.AddRange(spans);
				else if (nextNode != null)
					nextNode.ILSpans.AddRange(spans);
				else if (prevNode != null)
					block.EndILSpans.AddRange(spans);
				else
					block.ILSpans.AddRange(spans);
			}
			else {
				if (nextNode != null)
					nextNode.ILSpans.AddRange(spans);
				else if (prevNode != null) {
					if (prevNode.SafeToAddToEndILSpans)
						prevNode.EndILSpans.AddRange(spans);
					else
						block.EndILSpans.AddRange(spans);
				}
				else
					block.ILSpans.AddRange(spans);
			}
		}

		public static void AddILSpansTryPreviousFirst(ILInstruction removed, ILInstruction prev, ILInstruction next, Block block)
		{
			if (removed == null)
				return;
			AddILSpansTryPreviousFirst(prev, next, block, removed);
		}

		public static void AddILSpansTryNextFirst(ILInstruction removed, ILInstruction prev, ILInstruction next, Block block)
		{
			if (removed == null)
				return;
			AddILSpansTryNextFirst(prev, next, block, removed);
		}

		public static void AddILSpansTryPreviousFirst(ILInstruction prev, ILInstruction next, Block block, ILInstruction removed)
		{
			if (prev != null && prev.SafeToAddToEndILSpans)
				removed.AddSelfAndChildrenRecursiveILSpans(prev.EndILSpans);
			else if (next != null)
				removed.AddSelfAndChildrenRecursiveILSpans(next.ILSpans);
			else if (prev != null)
				removed.AddSelfAndChildrenRecursiveILSpans(block.EndILSpans);
			else
				removed.AddSelfAndChildrenRecursiveILSpans(block.ILSpans);
		}

		public static void AddILSpansTryNextFirst(ILInstruction prev, ILInstruction next, Block block, ILInstruction removed)
		{
			if (next != null)
				removed.AddSelfAndChildrenRecursiveILSpans(next.ILSpans);
			else if (prev != null) {
				if (prev.SafeToAddToEndILSpans)
					removed.AddSelfAndChildrenRecursiveILSpans(prev.EndILSpans);
				else
					removed.AddSelfAndChildrenRecursiveILSpans(block.EndILSpans);
			}
			else
				removed.AddSelfAndChildrenRecursiveILSpans(block.ILSpans);
		}

		public static void AddILSpansTryNextFirst(ILInstruction prev, ILInstruction next, Block block, IEnumerable<ILSpan> ilSpans)
		{
			if (next != null)
				next.ILSpans.AddRange(ilSpans);
			else if (prev != null) {
				if (prev.SafeToAddToEndILSpans)
					prev.EndILSpans.AddRange(ilSpans);
				else
					block.EndILSpans.AddRange(ilSpans);
			}
			else
				block.ILSpans.AddRange(ilSpans);
		}

		public static void AddILSpansTryPreviousFirst(IList<ILInstruction> newBody, IList<ILInstruction> body, int removedIndex, Block block)
		{
			ILInstruction prev = newBody.Count > 0 ? newBody[newBody.Count - 1] : null;
			ILInstruction next = removedIndex + 1 < body.Count ? body[removedIndex + 1] : null;
			AddILSpansTryPreviousFirst(body[removedIndex], prev, next, block);
		}

		public static void AddILSpansTryNextFirst(IList<ILInstruction> newBody, IList<ILInstruction> body, int removedIndex, Block block)
		{
			ILInstruction prev = newBody.Count > 0 ? newBody[newBody.Count - 1] : null;
			ILInstruction next = removedIndex + 1 < body.Count ? body[removedIndex + 1] : null;
			AddILSpansTryNextFirst(body[removedIndex], prev, next, block);
		}

		/// <summary>
		/// Adds the removed instruction's ILSpans to the next or previous instruction
		/// </summary>
		/// <param name="block">The owner block</param>
		/// <param name="body">Body</param>
		/// <param name="removedIndex">Index of removed instruction</param>
		public static void AddILSpans(Block block, IList<ILInstruction> body, int removedIndex)
		{
			AddILSpans(block, body, removedIndex, 1);
		}

		/// <summary>
		/// Adds the removed instruction's ILSpans to the next or previous instruction
		/// </summary>
		/// <param name="block">The owner block</param>
		/// <param name="body">Body</param>
		/// <param name="removedIndex">Index of removed instruction</param>
		/// <param name="numRemoved">Number of removed instructions</param>
		public static void AddILSpans(Block block, IList<ILInstruction> body, int removedIndex, int numRemoved)
		{
			var prev = removedIndex - 1 >= 0 ? body[removedIndex - 1] : null;
			var next = removedIndex + numRemoved < body.Count ? body[removedIndex + numRemoved] : null;

			ILInstruction node = next ?? prev;

			for (int i = 0; i < numRemoved; i++)
				AddILSpansToInstruction(node, prev, next, block, body[removedIndex + i]);
		}

		public static void AddILSpans(Block block, IList<ILInstruction> body, int removedIndex, IEnumerable<ILSpan> ilSpans)
		{
			var prev = removedIndex - 1 >= 0 ? body[removedIndex - 1] : null;
			var next = removedIndex + 1 < body.Count ? body[removedIndex + 1] : null;

			ILInstruction node = next ?? prev;

			AddILSpansToInstruction(node, prev, next, block, ilSpans);
		}

		public static void AddILSpansToInstruction(ILInstruction nodeToAddTo, ILInstruction prev, ILInstruction next, Block block, ILInstruction removed)
		{
			Debug.Assert(nodeToAddTo == prev || nodeToAddTo == next || nodeToAddTo == block);
			if (nodeToAddTo != null) {
				if (nodeToAddTo == prev && prev.SafeToAddToEndILSpans) {
					removed.AddSelfAndChildrenRecursiveILSpans(prev.EndILSpans);
					return;
				}
				else if (nodeToAddTo != null && nodeToAddTo == next) {
					removed.AddSelfAndChildrenRecursiveILSpans(next.ILSpans);
					return;
				}
			}
			AddILSpansTryNextFirst(prev, next, block, removed);
		}

		public static void AddILSpansToInstruction(ILInstruction nodeToAddTo, ILInstruction prev, ILInstruction next, Block block, IEnumerable<ILSpan> ilSpans)
		{
			Debug.Assert(nodeToAddTo == prev || nodeToAddTo == next || nodeToAddTo == block);
			if (nodeToAddTo != null) {
				if (nodeToAddTo == prev && prev.SafeToAddToEndILSpans) {
					prev.EndILSpans.AddRange(ilSpans);
					return;
				}
				else if (nodeToAddTo != null && nodeToAddTo == next) {
					next.ILSpans.AddRange(ilSpans);
					return;
				}
			}
			AddILSpansTryNextFirst(prev, next, block, ilSpans);
		}
	}
}
