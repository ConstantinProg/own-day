namespace OwnDay.Domain;

public readonly record struct UserId(long Value)
{
    public bool IsValid => Value > 0;
}
