﻿using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using Pixel3D.Animations;
using Pixel3D.Animations.Serialization;
using Pixel3D.Serialization;
using Pixel3D.Serialization.Context;

namespace Pixel3D
{
	/// <summary>Reference to a <see cref="Sprite"/> stored in a <see cref="ImageBundle"/></summary>
	public struct SpriteRef
    {
        private ImageBundle bundle;
        private int index;
        private Point origin; // <- Stored here because it makes sprite de-duplication simple


        /// <summary>
        /// Create a reference to the given sprite in a dummy sprite bundle.
        /// NOTE: Expects the texture of the sprite to be either immutable or unshared (won't get uncached or modified)
        /// </summary>
        public SpriteRef(Sprite sprite)
        {
            this.bundle = new ImageBundle(sprite);
            this.index = 0;
            this.origin = sprite.origin;
        }



        #region Animation Serialization

        public void Serialize(AnimationSerializeContext context)
        {
            // Bundling is handled by registering images, keyed on the sprite itself, so we just pass-through:
            ResolveRequire().Serialize(context);
        }

        public SpriteRef(AnimationDeserializeContext context)
        {
            // IMPORTANT: This method is compatible with Sprite's deserializer

            if(context.imageBundle != null)
            {
                this.bundle = context.imageBundle;
                // NOTE: AssetTool depends on us not actually resolving the sprite during load

                this.index = context.br.ReadInt32();
                if(index != -1)
                    this.origin = context.br.ReadPoint();
                else
                    this.origin = default(Point);
            }
            else // In place sprite
            {
                Sprite sprite = new Sprite(context); // Pass through

                this.bundle = new ImageBundle(sprite);
                this.index = 0;
                this.origin = sprite.origin;
            }
        }

        #endregion




        /// <summary>
        /// Resolve this sprite reference, if it is already loaded. Otherwise queue it for loading.
        /// IMPORTANT: The result of this method is not network-safe! Use for rendering only.
        /// </summary>
        public bool ResolveBestEffort(out Sprite sprite)
        {
            // This API is capable of handling complicated threading scenarios where
            // the texture might not be ready, and we might have to pre-warm a cache and all
            // other kinds of nonsense. As it turns out, loading is far more reliable on the,
            // main thread, and far simpler if we load just-in-time, and - as it turns out -
            // we are fast enough that this will work.

            sprite = bundle.GetSprite(index, origin); // <- implicitly touches the bundle and makes it immediately ready to draw
            return true;
        }


        /// <summary>Resolve this sprite reference, or fail if the sprite is cachable at all (do not use except in editor / tools).</summary>
        public Sprite ResolveRequire()
        {
            if(bundle.IsCachable)
            {
                Debug.Assert(false);
                throw new InvalidOperationException();
            }

            return bundle.GetSprite(index, origin);
        }




        #region Network Serializer (definition only)

        // Definition-only (TODO: Maybe we should care that the sprite references match up between players?)
        [CustomSerializer] public static void Serialize(SerializeContext context, BinaryWriter bw, ref SpriteRef value) { }
        [CustomSerializer] public static void Deserialize(DeserializeContext context, BinaryReader br, ref SpriteRef value) { throw new InvalidOperationException(); }

        #endregion



    }
}
