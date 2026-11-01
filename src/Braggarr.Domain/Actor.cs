namespace Braggarr.Domain;

public enum ActorKind
{
	/// <summary>The one person who may approve: signed in to the UI, or merging the pull request on GitHub.</summary>
	Owner,

	/// <summary>Anything holding an API key. It can propose; it can never approve.</summary>
	Agent,

	/// <summary>Braggarr itself: the scheduler and the sync.</summary>
	System,

	/// <summary>
	/// Claude, judging a proposal against the owner's criteria. It may approve, only when the owner turned that on and every
	/// criterion passed; what it approves waits a delay the owner set before anything goes out.
	/// </summary>
	Judge
}

/// <summary>Who asked for a change. <see cref="Name"/> is the GitHub login for the owner and the key's name for an agent.</summary>
public sealed record Actor(ActorKind Kind, string Name)
{
	public static Actor System { get; } = new(ActorKind.System, "braggarr");

	public static Actor Owner(string login) => new(ActorKind.Owner, login);

	public static Actor Agent(string keyName) => new(ActorKind.Agent, keyName);

	public static Actor Judge { get; } = new(ActorKind.Judge, "claude");

	/// <summary>Who may approve a campaign: the owner, and the judge. Never an agent.</summary>
	public bool MayApprove => Kind is ActorKind.Owner or ActorKind.Judge;

	public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Name}";
}
