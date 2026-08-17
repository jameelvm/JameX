using JameX.Identity.Data;
using JameX.Identity.Domain;
using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;

namespace JameX.Identity.Repositories;

/// <summary>
/// Every way this service reaches <c>users</c>, and nothing else.
/// <para>
/// Note what is <b>not</b> here: no <c>IRepository&lt;T&gt;</c> with
/// <c>GetAll()</c> and <c>Find(predicate)</c>. A generic repository over EF Core
/// re-wraps <see cref="DbSet{T}"/> in a worse <see cref="DbSet{T}"/> — it hides
/// nothing, and an <c>IQueryable</c> escaping through it lets a caller compose
/// arbitrary queries, which is exactly the coupling the abstraction was supposed
/// to prevent.
/// </para>
/// <para>
/// These methods are named for the questions the application actually asks. The
/// payoff is that the query lives next to the index that serves it, and a
/// service can be unit tested against a fake without a database.
/// </para>
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct);

    Task<IReadOnlyList<User>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);

    Task<bool> ExistsAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Inserts the user, or reports that the email is taken.
    /// <para>
    /// Returning <c>false</c> rather than letting <see cref="DbUpdateException"/>
    /// escape is the boundary doing its job: the service layer above decides
    /// what a duplicate <i>means</i>, without knowing that Postgres said 23505.
    /// </para>
    /// </summary>
    Task<bool> TryAddAsync(User user, CancellationToken ct);
}

internal sealed class UserRepository(IdentityDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

    public async Task<IReadOnlyList<User>> GetManyAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];

        var ids = userIds.Distinct().ToArray();

        // Translates to `WHERE id = ANY(@ids)` — one indexed statement for the
        // whole batch, which is the entire point of the batch endpoint.
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToListAsync(ct);
    }

    public Task<bool> ExistsAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct);

    public async Task<bool> TryAddAsync(User user, CancellationToken ct)
    {
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(out _))
        {
            // The failed insert is still sitting in the change tracker; leaving
            // it there would make the next SaveChanges on this scoped context
            // retry the same doomed write.
            db.Entry(user).State = EntityState.Detached;
            return false;
        }
    }
}
