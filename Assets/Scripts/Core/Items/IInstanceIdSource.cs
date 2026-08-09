namespace Dropshock.Core.Items
{
    /// <summary>
    /// Allocates unique <see cref="ItemStack.InstanceId"/> values.
    ///
    /// <para><b>Server-side only, and never client-side.</b> An instance id is the
    /// audit handle for a physical item: it is what makes duplication detectable
    /// after the fact, because the same instance in two places is a bug or an
    /// exploit. A client that could mint ids could mint items.</para>
    ///
    /// <para>Ids must be unique across <em>the account store</em>, not merely across
    /// one raid — an item created in a raid is later persisted to the stash and sits
    /// alongside items from every other session. A per-process counter starting at 1
    /// would collide the moment two servers persist to the same database.</para>
    /// </summary>
    public interface IInstanceIdSource
    {
        /// <summary>Returns an id never previously issued. Never returns 0.</summary>
        uint Next();
    }

    /// <summary>
    /// In-memory id source for a single raid session.
    ///
    /// <para><b>Not suitable for persistence.</b> It guarantees uniqueness only
    /// within this process and only since it was constructed. Items that survive to
    /// the stash must be reissued authoritative ids by the backend at extraction, or
    /// two servers will hand different items the same id and the audit trail becomes
    /// worthless. See ARCHITECTURE.md, "Stash and persistence".</para>
    /// </summary>
    public sealed class SessionInstanceIdSource : IInstanceIdSource
    {
        private uint _next;

        /// <param name="seed">First id to issue. Non-zero; 0 is reserved.</param>
        public SessionInstanceIdSource(uint seed = 1)
        {
            _next = seed == 0 ? 1u : seed;
        }

        public uint Next()
        {
            uint id = _next;

            // Wrapping would start reissuing live ids. At raid scale this is
            // unreachable, but a silent wrap is the kind of thing that only shows up
            // as an inexplicable dupe months later.
            if (_next == uint.MaxValue)
            {
                throw new System.InvalidOperationException(
                    "SessionInstanceIdSource exhausted; ids would wrap and collide.");
            }

            _next++;
            return id;
        }
    }
}
