using System;
using System.Collections.Generic;

namespace Rupture
{

internal sealed class CommitTransaction
{
	private readonly List<(Action Apply, Action Undo)> _steps = new List<(Action, Action)>();
	internal void Add(Action apply, Action undo) => _steps.Add((apply, undo));
	internal void Commit()
	{
		int index = 0;
		try
		{
			for (; index < _steps.Count; index++) _steps[index].Apply();
		}
		catch (Exception failure)
		{
			List<Exception> errors = new List<Exception> { failure };
			// Include the failing operation: it may have mutated before throwing.
			for (int undo = Math.Min(index, _steps.Count - 1); undo >= 0; undo--)
				try { _steps[undo].Undo(); } catch (Exception rollback) { errors.Add(rollback); }
			if (errors.Count > 1) throw new AggregateException("Commit and rollback failed.", errors);
			throw;
		}
	}
}
}
