using System;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

// A private, short-lived support entry opens EFT's normal simulation gate without
// changing simulation mode or manually stepping the gameplay scene.
internal sealed class PhysicsBootstrapProbeLease : IDisposable
{
	private Rigidbody _body;

	internal static IDisposable TrySchedule(Rigidbody body)
	{
		if (body == null || !PhysicsExtensions.UpdateController.Enabled) return null;
		PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f, null);
		return new PhysicsBootstrapProbeLease { _body = body };
	}

	public void Dispose()
	{
		Rigidbody body = _body;
		if (ReferenceEquals(body, null)) return;
		_body = null;
		if (PhysicsExtensions.UpdateController.Enabled)
			PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
		else
			// UnsupportRigidbody is a no-op while disabled. Still retire our private
			// entry if teardown disabled the controller before disposing the backend.
			PhysicsExtensions.UpdateController._rigidbodies.RemoveAll(entry => ReferenceEquals(entry.rigidbody, body));
	}
}
