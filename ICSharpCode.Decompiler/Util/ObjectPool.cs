using System;
using System.Collections.Generic;

namespace ICSharpCode.Decompiler.Util
{
	/// <summary>
	/// Object pool. It's not thread safe.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	internal sealed class ObjectPool<T> where T : class
	{
		private readonly Func<T> create;
		private readonly Action<T> initialize;

		/// <summary>
		/// All allocated objects. Used to restore <see cref="freeObjs"/> when <see cref="ReuseAllObjects"/>
		/// gets called.
		/// </summary>
		private readonly List<T> allObjs;

		/// <summary>
		/// All free objects. A subset of <see cref="allObjs"/>
		/// </summary>
		private readonly List<T> freeObjs;

		public ObjectPool(Func<T> create, Action<T> initialize)
		{
			this.create = create;
			this.initialize = initialize;
			this.allObjs = new List<T>();
			this.freeObjs = new List<T>();
		}

		public T Allocate()
		{
			if (freeObjs.Count > 0)
			{
				int i = freeObjs.Count - 1;
				var o = freeObjs[i];
				freeObjs.RemoveAt(i);
				initialize?.Invoke(o);
				return o;
			}

			var newObj = create();
			allObjs.Add(newObj);
			return newObj;
		}

		public void Free(T obj)
		{
			freeObjs.Add(obj);
		}

		public void ReuseAllObjects()
		{
			freeObjs.Clear();
			freeObjs.AddRange(allObjs);
		}
	}
}
