using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.CSharp
{
	/// <summary>
	/// Shared by all code in the current decompiler thread. It must only be accessed from the
	/// owning thread.
	/// </summary>
	public sealed class DecompilerCache {
		private readonly ObjectPool<IAstTransform[]> csharpPipelinePool;

		public DecompilerCache(DecompilerContext ctx) {
			this.csharpPipelinePool = new ObjectPool<IAstTransform[]>(() => CSharpTransformationPipeline.CreatePipeline(ctx), null);
		}

		public void Reset() {
			csharpPipelinePool.ReuseAllObjects();
		}

		public IAstTransform[] GetCSharpPipeline() {
			return csharpPipelinePool.Allocate();
		}

		public void Return(IAstTransform[] pipeline) {
			csharpPipelinePool.Free(pipeline);
		}
	}
}
