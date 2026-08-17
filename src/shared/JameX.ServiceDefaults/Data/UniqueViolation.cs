using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JameX.ServiceDefaults.Data;

/// <summary>
/// Turns a Postgres unique-index violation into something an API layer can map
/// to <c>409 Conflict</c>.
/// <para>
/// The tempting alternative — <c>if (await db.Users.AnyAsync(...)) return
/// Conflict();</c> — is a race. Two concurrent registrations both read "absent"
/// and both proceed to insert; the index rejects one of them anyway, but now it
/// surfaces as an unhandled 500 instead of a 409. Letting the database be the
/// arbiter and translating its answer is both correct and one round trip
/// cheaper.
/// </para>
/// </summary>
public static class UniqueViolation
{
    /// <summary>SQLSTATE 23505 — <c>unique_violation</c>.</summary>
    private const string UniqueViolationSqlState = "23505";

    /// <summary>
    /// True if this failure was a unique-index violation, and if so which index
    /// rejected the write — needed because a table may have several, and
    /// "email already taken" and "handle already taken" are different messages.
    /// </summary>
    public static bool IsUniqueViolation(this DbUpdateException exception, out string? constraintName)
    {
        if (exception.InnerException is PostgresException { SqlState: UniqueViolationSqlState } postgres)
        {
            constraintName = postgres.ConstraintName;
            return true;
        }

        constraintName = null;
        return false;
    }
}
