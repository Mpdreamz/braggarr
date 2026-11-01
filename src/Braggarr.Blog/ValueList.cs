using System.Collections;
using System.Runtime.CompilerServices;

namespace Braggarr.Blog;

/// <summary>
/// An immutable list with value equality, so records holding lists compare by content. Records compare <c>IReadOnlyList</c>
/// members by reference, which would make two identical sites read from the same files unequal. A copy of the domain's: the
/// blog depends on nothing of Braggarr's own.
/// </summary>
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
	private readonly T[] _items;

#pragma warning disable CA1000 // the empty list belongs to its element type, like ImmutableArray<T>.Empty
	public static ValueList<T> Empty { get; } = new([]);
#pragma warning restore CA1000

	internal ValueList(T[] items) => _items = items;

	public int Count => _items.Length;

	public T this[int index] => _items[index];

	public ValueList<T> Add(T item) => new([.. _items, item]);

	public ValueList<T> Replace(Func<T, bool> match, T item) => new([.. _items.Select(i => match(i) ? item : i)]);

	public bool Equals(ValueList<T>? other) =>
		other is not null && (ReferenceEquals(this, other) || _items.AsSpan().SequenceEqual(other._items, EqualityComparer<T>.Default));

	public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (var item in _items)
		{
			hash.Add(item);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => $"[{string.Join(", ", _items)}]";

	public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

	public static bool operator ==(ValueList<T>? left, ValueList<T>? right) => Equals(left, right);

	public static bool operator !=(ValueList<T>? left, ValueList<T>? right) => !Equals(left, right);
}

public static class ValueList
{
	public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => items.IsEmpty ? ValueList<T>.Empty : new(items.ToArray());

	public static ValueList<T> ToValueList<T>(this IEnumerable<T> items) => new([.. items]);
}
