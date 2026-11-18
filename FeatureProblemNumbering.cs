using MySqlConnector;

public sealed partial class MySqlStore
{
    private static async Task<long> AllocateProblemNumber(MySqlConnection connection, MySqlTransaction transaction, CancellationToken ct)
    {
        // The existing allocator row is locked until problem creation commits.
        // Seed without scanning problems first, avoiding a competing seed race.
        await using (var seed = new MySqlCommand("INSERT INTO astra_sequence_state(name,next_value) VALUES('problem_number_seq',1) ON DUPLICATE KEY UPDATE name=VALUES(name)", connection, transaction))
            await seed.ExecuteNonQueryAsync(ct);
        long next;
        await using (var counter = new MySqlCommand("SELECT next_value FROM astra_sequence_state WHERE name='problem_number_seq' FOR UPDATE", connection, transaction))
            next = Convert.ToInt64(await counter.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        await using (var maximum = new MySqlCommand("SELECT COALESCE(MAX(number),0) FROM problems", connection, transaction))
            next = Math.Max(next, checked(Convert.ToInt64(await maximum.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) + 1));
        if (next < 1) throw new InvalidOperationException("The problem numbering counter is invalid.");
        await using (var advance = new MySqlCommand("UPDATE astra_sequence_state SET next_value=@next WHERE name='problem_number_seq'", connection, transaction))
        {
            advance.Parameters.AddWithValue("@next", checked(next + 1));
            await advance.ExecuteNonQueryAsync(ct);
        }
        return next;
    }
}
