using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Diagnostics;
using Pixel3D.Extensions;
using Pixel3D.Serialization;


namespace Pixel3D.Animations
{
    public class AnimationFrame
    {
        public AnimationFrame()
        {
            masks = new TagLookup<Mask>();

            outgoingAttachments = new TagLookup<OutgoingAttachment>();
            incomingAttachments = new TagLookup<Position>();
            triggers = null;
        }


        public Cel firstLayer;

        public LayersList layers { get { return new LayersList(this); } }

        public Position shadowOffset;

        // Animation:
        /// <summary>Number of ticks (frames at 60FPS) that this animation frame lasts for.</summary>
        public int delay;

        // Gameplay:
        /// <summary>Gameplay position offset to apply at the start of this frame.</summary>
        public Position positionDelta;
        public bool SnapToGround { get; set; }

        /// <summary>The layer number where attachments are inserted (before the given layer)</summary>
        public int attachAtLayer;
        /// <summary>True if the layers following attachAtLayer can be drawn over a held sorted object (a "thick"/3D object)</summary>
        /// <remarks>
        /// This is mostly a a backwards-compatibility thing, given that our old animations are not set-up to support this properly.
        /// But it is conceivable that it will be a useful feature for animations that can pick up thick and thin objects.
        /// Basically says: everything above "attachAtLayer" is small -- like a hand or similar.
        /// </remarks>
        public bool canDrawLayersAboveSortedAttachees;


        public TagLookup<OutgoingAttachment> outgoingAttachments;
        public TagLookup<Position> incomingAttachments;

        // TODO: This could be converted to a Dictionary with a lone fallback (so could outgoingAttagments and maybe incomming attachments)
        public readonly TagLookup<Mask> masks;

        /// <summary>List of symbols, or null for no triggers this frame.</summary>
        public List<string> triggers;

        public string cue;

        public void AddTrigger(string symbol)
        {
            if(triggers == null)
                triggers = new List<string>();
            triggers.Add(symbol);
        }

        public bool RemoveTrigger(string symbol)
        {
            if (triggers == null)
                return false;
            return triggers.Remove(symbol);
        }


        public AnimationFrame(Texture2D texture, Rectangle sourceRectangle, Point origin, int delay) : this()
        {
            this.layers.Add(new Cel(new Sprite(texture, sourceRectangle, origin)));
            this.delay = delay;
        }

        public AnimationFrame(Cel cel, int delay) : this()
        {
            this.layers.Add(cel);
            this.delay = delay;
        }

        public AnimationFrame(int delay) : this()
        {
            this.delay = delay;
        }




        #region Alpha Mask and Bounds Handling

        /// <summary>Calculate the maximum world space bounds of all layers in the frame. EDITOR ONLY!</summary>
        public Rectangle CalculateGraphicsBounds()
        {
            Rectangle maxBounds = Rectangle.Empty;
            foreach(var celView in layers)
            {
                maxBounds = RectangleExtensions.UnionIgnoreEmpty(maxBounds, celView.CalculateGraphicsBounds());
            }
            return maxBounds;
        }


        public Mask GetAlphaMaskView()
        {
            Debug.Assert(!Asserts.enabled || masks.HasBaseFallback); // <- the alpha mask should have been generated by this point

            Mask mask = masks.GetBaseFallback();
            Debug.Assert(!Asserts.enabled || mask.isGeneratedAlphaMask == true);

            return mask;
        }


        /// <param name="celAlphaMasks">Fill with masks created from Cels</param>
        /// <param name="allMasks">Fill with all created masks</param>
        public void RegenerateAlphaMask()
        {
            masks.TryRemoveBaseFallBack(); // <- it is about to be regenerated

            // If this frame has a single sprite-containing Cel, generate it directly
            if(firstLayer != null && firstLayer.next == null)
            {
                Mask mask = new Mask
                {
                    data = firstLayer.spriteRef.ResolveRequire().GetAlphaMask(),
                    isGeneratedAlphaMask = true,
                };
                    
                masks.Add(new TagSet(), mask);
            }
            else // ... Otherwise, try to create a mask merged from the frame's layers
            {
                List<MaskData> layerMasks = new List<MaskData>();
                foreach(var cel in layers)
                {
                    MaskData maskData = cel.spriteRef.ResolveRequire().GetAlphaMask();
                    if(maskData.Width != 0 && maskData.Height != 0)
                        layerMasks.Add(maskData);
                }

                Rectangle maxBounds = Rectangle.Empty;
                foreach(var maskData in layerMasks)
                {
                    maxBounds = RectangleExtensions.UnionIgnoreEmpty(maxBounds, maskData.Bounds);
                }

                Mask mask = new Mask() { isGeneratedAlphaMask = true };
                mask.data = new MaskData(maxBounds);
                foreach(var layerMask in layerMasks)
                {
                    Debug.Assert(!Asserts.enabled || mask.data.Bounds.Contains(layerMask.Bounds));
                    mask.data.SetBitwiseOrFrom(layerMask);
                }

                masks.Add(new TagSet(), mask);
            }
        }




        #endregion



        #region Rendering

        public void Draw(DrawContext drawContext, Position position, bool flipX)
        {
            Draw(drawContext, position, flipX, Color.White);
        }

        public void Draw(DrawContext drawContext, Position position, bool flipX, Color color)
        {
            foreach(var layer in layers)
                layer.Draw(drawContext, position, flipX, color);
        }

        public void DrawBeforeAttachment(DrawContext drawContext, Position position, bool flipX, Color color)
        {
            Debug.Assert(attachAtLayer >= 0);
            // NOTE: Iterating this way because layers is a linked list...
            int i = 0;
            foreach(var layer in layers)
            {
                if(i < attachAtLayer)
                    layer.Draw(drawContext, position, flipX, color);
                else
                    break;
                i++;
            }
        }

        public void DrawAfterAttachment(DrawContext drawContext, Position position, bool flipX, Color color)
        {
            Debug.Assert(attachAtLayer >= 0);
            // NOTE: Iterating this way because layers is a linked list...
            int i = 0;
            foreach(var layer in layers)
            {
                if(i >= attachAtLayer)
                    layer.Draw(drawContext, position, flipX, color);
                i++;
            }
        }



        public void GetShadowReceiverHeightmapViews(Position position, bool flipX, List<HeightmapView> output)
        {
            foreach(var layer in layers)
            {
                if(layer.shadowReceiver != null)
                {
                    output.Add(new HeightmapView(layer.shadowReceiver.heightmap, position, flipX));
                }
            }
        }


        #endregion



        #region Soft Rendering

        public Data2D<Color> SoftRender()
        {
            Data2D<Color> output = new Data2D<Color>();
            foreach(var cel in layers)
            {
                if(cel.shadowReceiver != null)
                    continue; // Skip shadow receivers

                var spriteData = cel.spriteRef.ResolveRequire().GetData();

                // Blend with the output
                if(output.Data == null)
                    output = spriteData;
                else
                {
                    output = output.LazyCopyExpandToContain(spriteData.Bounds);

                    for(int y = spriteData.StartY; y < spriteData.EndY; y++) for(int x = spriteData.StartX; x < spriteData.EndX; x++)
                    {
                        Color pixel = spriteData[x, y];
                        if(pixel != Color.Transparent)
                        {
                            if(pixel.A == 0xFF)
                                output[x, y] = pixel;
                            else
                            {
                                Vector4 pixelFloat = pixel.ToVector4();
                                output[x, y] = new Color(pixelFloat + output[x, y].ToVector4() * (1f - pixelFloat.W)); // floating-point blend, because I'm lazy...
                            }
                        }
                    }
                }
            }

            Debug.Assert(!Asserts.enabled || output.Bounds == GetSoftRenderBounds());

            return output;
        }

        /// <summary>EDITOR ONLY!</summary>
        public Rectangle GetSoftRenderBounds()
        {
            Rectangle output = Rectangle.Empty;
            foreach(var cel in layers)
            {
                if(cel.shadowReceiver != null)
                    continue; // Skip shadow receivers

                Sprite sprite = cel.spriteRef.ResolveRequire();
                Rectangle bounds = new Rectangle(-sprite.origin.X, -sprite.origin.Y, sprite.sourceRectangle.Width, sprite.sourceRectangle.Height);

                output = RectangleExtensions.UnionIgnoreEmpty(output, bounds);
            }
            return output;
        }

        #endregion



        #region Serialize

        [SerializationIgnoreDelegates]
        public void Serialize(AnimationSerializeContext context)
        {
            context.bw.Write(delay);
            context.bw.Write(positionDelta);
            context.bw.Write(shadowOffset);

            context.bw.Write(SnapToGround);

            // NOTE: This walks the layer linked list twice, but is only O(n), so no biggie
            int layerCount = layers.Count;
            context.bw.Write(layerCount);
            foreach(var cel in layers)
                cel.Serialize(context);

            masks.Serialize(context, m => m.Serialize(context));

            outgoingAttachments.Serialize(context, oa => oa.Serialize(context));
            incomingAttachments.Serialize(context, p => context.bw.Write(p));

            if(triggers == null)
                context.bw.Write((int)0);
            else
            {
                context.bw.Write(triggers.Count);
                for(int i = 0; i < triggers.Count; i++)
                    context.bw.Write(triggers[i]);
            }

            context.bw.Write(attachAtLayer.Clamp(0, layers.Count));
            context.bw.Write(canDrawLayersAboveSortedAttachees);
            
            context.bw.WriteNullableString(cue);
        }

        /// <summary>Deserialize into new object instance</summary>
        [SerializationIgnoreDelegates]
        public AnimationFrame(AnimationDeserializeContext context)
        {
            delay = context.br.ReadInt32();
            positionDelta = context.br.ReadPosition();
            shadowOffset = context.br.ReadPosition();

            SnapToGround = context.br.ReadBoolean();

            int layersCount = context.br.ReadInt32();
            if(layersCount > 0)
            {
                Cel currentCel = null;
                this.firstLayer = currentCel = new Cel(context);
                for(int i = 1; i < layersCount; i++)
                {
                    currentCel.next = new Cel(context);
                    currentCel = currentCel.next;
                }
            }

            masks = new TagLookup<Mask>(context, () => new Mask(context));

            outgoingAttachments = new TagLookup<OutgoingAttachment>(context, () => new OutgoingAttachment(context));
            incomingAttachments = new TagLookup<Position>(context, () => context.br.ReadPosition());

            int triggerCount = context.br.ReadInt32();
            if(triggerCount > 0)
            {
                triggers = new List<string>(triggerCount);
                for(int i = 0; i < triggerCount; i++)
                    triggers.Add(context.br.ReadString());
            }

            attachAtLayer = context.br.ReadInt32();
            canDrawLayersAboveSortedAttachees = context.br.ReadBoolean();

            cue = context.br.ReadNullableString();
        }

        #endregion



        #region Attachments

        public void AddOutgoingAttachment(TagSet rule, OutgoingAttachment outgoingAttachment)
        {
            if (rule != null)
                outgoingAttachments.Add(rule, outgoingAttachment);
        }

        public void AddIncomingAttachment(TagSet rule, Position position)
        {
            if (rule != null)
                incomingAttachments.Add(rule, position);
        }

        public bool RemoveOutgoingAttachment(OutgoingAttachment outgoingAttachment)
        {
            for (int i = 0; i < outgoingAttachments.Count; i++)
            {
                if (ReferenceEquals(outgoingAttachments.Values[i], outgoingAttachment))
                {
                    outgoingAttachments.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public bool RemoveMask(Mask mask)
        {
            for (int i = 0; i < masks.Count; i++)
            {
                if (ReferenceEquals(masks.Values[i], mask))
                {
                    masks.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        #endregion


    }
}
