# File token intent and ordered resource recording

Historical source cohorts 4533669fa5231368f17c4b59b17c3e2f52e24a89 (four paths)
and 25418c95b5ac79400029ce274541f0e51728da3e (six paths) require workload identity
intent and a ServiceAccount-before-Deployment application protocol. Accepted
File67 proved parsed identity/IAM/source admission and ordered plan resources;
it did not execute or prove a resource-application boundary.

The admitted KSA already requires boolean automountServiceAccountToken=true.
An explicit Pod override now also must be boolean true. False, strings and null
fail before IAM/image callbacks. An absent Pod override inherits the admitted
KSA setting, preserving Kubernetes precedence rather than silently inserting a
field. [Kubernetes service-account configuration](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/)
documents that a Pod override takes precedence over the ServiceAccount setting.
This is token intent validation, not proof of GKE token projection or live WIF.

The actual dormant template remains unchanged: replicas zero and Pod automount
false. Its existing real-template consumer case now requires opaque token
admission rejection and zero callbacks while retaining those disabled settings.
No plan or script repairs, enables or applies that template automatically.
The other accepted metadata, producer-byte, IAM, source, input-preservation and
callback guards remain in the suite.

`Invoke-LegacyFileIdentityResourceApplication` is a recording-only adapter
boundary. It requires both RecordingOnly and an injected RecordResource callback;
there is no default command, cloud or provider implementation. It constructs the
plan through the existing actual identity/IAM/source/image/metadata admission,
instead of accepting a caller-asserted prevalidated plan. Source observation is
repeated before each resource callback and after the final acknowledgement. The
existing producer-byte validation is reused, and both source-guard files are
rechecked before import and every resource observation, including callback drift.

Callbacks receive immutable JSON copies in ServiceAccount, then Deployment order.
Each acknowledgement must be exactly a four-field hashtable: string Kind, Name
and Namespace matching the resource, and boolean Succeeded=true. Missing,
unknown, false, malformed, multiple or wrong-identity acknowledgements stop the
protocol. Callback output on other streams is captured and rejected alongside
the acknowledgement, keeping warnings/errors from escaping the boundary. The
same capture is applied to the IAM and image callbacks, whose existing single
string/hashtable receipt contracts reject additional stream output.
KSA failure or source drift after KSA recording prevents Deployment
invocation. Callback exceptions and receipt details are replaced by one opaque
error. Callback-local/input-argument changes cannot replace the admitted resources
or the source observation scope. Every result retains deploymentAllowed=false,
liveAccepted=false and snapshotAuthenticityAccepted=false.

The injected callbacks are trusted recording boundaries, not authenticated
provider receipts. Success proves ordered code dispatch only. It establishes no
resource creation, rollout health, cloud snapshot authenticity, exactly-once
application or rollback. Future live activation remains a separate decision.

Eight new Pod-intent cases extend the retained 75 identity-plan cases to 83.
Twenty-eight actual resource-recording cases cover success, input preservation,
missing adapter/mode, token/IAM/image/source admission, KSA failure/unknown receipt,
Deployment failure, producer-byte changes and source drift between/after recordings.
The expected native full suite is 893, derived from accepted 857 plus 36 new cases. Hosted Release,
full-suite, four raw coverage floors and static checks remain required before
acceptance; passive scenario execution is not native .NET validation.
