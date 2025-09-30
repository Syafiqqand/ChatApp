using System;

namespace ChatServer
{
    public static class UIDGenerator
    {
        /// <summary>
        /// Menghasilkan sebuah User ID yang unik secara global.
        /// </summary>
        /// <returns>String representasi dari GUID.</returns>
        public static string Generate()
        {
            // Menghasilkan ID unik global (GUID) dan mengubahnya menjadi string.
            // Contoh: "f4b5c6e0-8a1b-4f3c-9d2e-1a2b3c4d5e6f"
            return Guid.NewGuid().ToString();
        }
    }
}