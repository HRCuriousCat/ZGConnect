namespace ZGConnect
{
    public static class DeterministicHash
    {
        public static uint Hash(int seed, int x, int y, int salt)
        {
            unchecked
            {
                uint h = (uint)seed;
                h ^= (uint)x * 374761393u;
                h ^= (uint)y * 668265263u;
                h ^= (uint)salt * 2246822519u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return h ^ (h >> 16);
            }
        }

        public static float Hash01(int seed, int x, int y, int salt)
        {
            return (Hash(seed, x, y, salt) & 0xFFFFFF) / 16777215f;
        }
    }
}
