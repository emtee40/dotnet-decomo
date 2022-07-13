using System.Text;

using dnlib.DotNet;

using dnSpy.Contracts.Decompiler;

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;

namespace ICSharpCode.Decompiler.CSharp
{
	public partial class CSharpAstBuilder
	{
		private struct AsyncMethodBodyResult
		{
			public AsyncMethodBodyResult(EntityDeclaration methodNode, MethodDef method, BlockStatement body, MethodDebugInfoBuilder builder, ILFunction function)
			{
				this.MethodNode = methodNode;
				this.Method = method;
				this.Body = body;
				this.Builder = builder;
				this.IlFunction = function;
				this.CurrentMethodIsAsync = function.IsAsync;
				this.CurrentMethodIsYieldReturn = function.IsIterator;
			}

			public readonly EntityDeclaration MethodNode;

			public readonly MethodDef Method;

			public readonly BlockStatement Body;

			public readonly MethodDebugInfoBuilder Builder;

			public readonly ILFunction IlFunction;

			public readonly bool CurrentMethodIsAsync;

			public readonly bool CurrentMethodIsYieldReturn;
		}

		private sealed class AsyncMethodBodyDecompilationState
		{
			public readonly StringBuilder StringBuilder = new StringBuilder();
		}

		AsyncMethodBodyDecompilationState GetAsyncMethodBodyDecompilationState() {
			lock (asyncMethodBodyDecompilationStates) {
				if (asyncMethodBodyDecompilationStates.Count > 0) {
					var state = asyncMethodBodyDecompilationStates[asyncMethodBodyDecompilationStates.Count - 1];
					asyncMethodBodyDecompilationStates.RemoveAt(asyncMethodBodyDecompilationStates.Count - 1);
					return state;
				}
			}
			return new AsyncMethodBodyDecompilationState();
		}

		void Return(AsyncMethodBodyDecompilationState state) {
			lock (asyncMethodBodyDecompilationStates)
				asyncMethodBodyDecompilationStates.Add(state);
		}

		void WaitForBodies() {
			if (methodBodyTasks.Count == 0)
				return;
			try {
				for (int i = 0; i < methodBodyTasks.Count; i++) {
					var result = methodBodyTasks[i].GetAwaiter().GetResult();
					context.CancellationToken.ThrowIfCancellationRequested();

					result.MethodNode.AddChild(result.Body, Roles.Body);

					if (result.Builder is not null)
						result.MethodNode.AddAnnotation(result.Builder);

					if (result.IlFunction is not null)
					{
						//AddAnnotationsToDeclaration(result.IlFunction.Method, result.MethodNode, result.IlFunction);
						AddDefinesForConditionalAttributes(result.IlFunction);
						CleanUpMethodDeclaration(result.MethodNode, result.Body, result.IlFunction);
					}

					comments.Clear();
					comments.AddRange(result.MethodNode.GetChildrenByRole(Roles.Comment));
					for (int j = comments.Count - 1; j >= 0; j--) {
						var c = comments[j];
						c.Remove();
						result.MethodNode.InsertChildAfter(null, c, Roles.Comment);
					}
				}
			}
			finally {
				methodBodyTasks.Clear();
			}
		}

		void ClearCurrentMethodState() {
			context.CurrentMethodIsAsync = false;
			context.CurrentMethodIsYieldReturn = false;
		}
	}
}
