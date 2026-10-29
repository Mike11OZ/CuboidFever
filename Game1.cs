using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace CuboidFever;

public class Game1 : Game
{
    // ---- Configuration ----
    private const float ReachDistance = 5f; // Easy to tweak here!
    
    // ---- Core ----
    private GraphicsDeviceManager _graphics;
    private BasicEffect _effect;
    private BasicEffect _lineEffect;
    private Texture2D _atlas;
    private ParticleSystem _particleSystem = new();
    private World _world;
    private AlphaTestEffect _solidEffect;
    private bool _spawned;
    private bool _lWasDown;
    private bool _oWasDown;
    private bool _vWasDown;
    private byte _airId;

    // ---- Camera ----
    private Player _player = new();
    private Vector3 _camPos;
    private Vector3 _forward;
    private float _yaw, _pitch;

    // ---- Block interaction ----
    private MouseState _prevMouse;
    private bool _hasTarget;
    private (int x, int y, int z) _target, _targetPrev;
    private byte[] _hotbar;
    private string[] _hotbarNames;
    private int _slot = 2;

    // ---- HUD ----
    private SpriteBatch _sprites;
    private Texture2D _pixel;

    // ---- FPS counter and cap ----
    private int _frames;
    private double _lastFpsTime;
    private readonly int[] _fpsCaps = { 30, 60, 120, 0 }; // 0 = uncapped
    private int _capIndex = 1;                            // 0=30, 1=60, 2=120, 3=uncapped
    private bool _fWasDown;

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = false;
        _graphics.SynchronizeWithVerticalRetrace = false; // vsync off, so the cap is what limits FPS
        ApplyFpsCap();
    }

    protected override void LoadContent()
    {
        string dataRoot = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "cuboidfever");

        string textureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "cuboidfever",
            "textures",
            "block");

        string atlasConfigPath = Path.Combine(
            dataRoot,
            "texture_atlas.json");

        TextureAtlas.Initialize(
            GraphicsDevice,
            textureRoot,
            atlasConfigPath);

        _atlas = TextureAtlas.Instance.Texture;

        _particleSystem.Init(TextureAtlas.Instance);

        _effect = new BasicEffect(GraphicsDevice)
        {
            VertexColorEnabled = true,
            TextureEnabled = true,
            Texture = _atlas,
            Projection = Matrix.CreatePerspectiveFieldOfView(
                MathHelper.ToRadians(70f),
                GraphicsDevice.Viewport.AspectRatio,
                0.1f,
                500f)
        };

        _lineEffect = new BasicEffect(GraphicsDevice)
        {
            VertexColorEnabled = true
        };

        _sprites = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        BlockRegistry.Initialize(dataRoot);

        var blocks = BlockRegistry.Instance;

        _airId = blocks.GetId("cuboidfever:air");

        _hotbar = blocks.HotbarIds;

        _hotbarNames = _hotbar
            .Select(id => blocks.GetName(id)
                .Split(':').Last()
                .Replace('_', ' '))
            .ToArray();

        _world = new World(8008135);
        _camPos = new Vector3(128.5f, 100f, 128.5f); // looking-from point while the first columns load

        _solidEffect = new AlphaTestEffect(GraphicsDevice)
        {
            VertexColorEnabled = true,
            Texture = _atlas,
            AlphaFunction = CompareFunction.GreaterEqual,
            ReferenceAlpha = 128,
            Projection = _effect.Projection
        };
    }

    private void TrySpawn()
    {
        int sx = 128, sz = 128;
        if (!_world.IsColumnLoaded(sx, sz)) return;

        int sy = World.Height - 1;
        while (sy > 0 && !_world.IsSolid(sx, sy, sz)) sy--;
        _player.Position = new Vector3(sx + 0.5f, sy + 1.01f, sz + 0.5f);
        _camPos = _player.Eye;
        Mouse.SetPosition(Window.ClientBounds.Width / 2, Window.ClientBounds.Height / 2);
        _spawned = true;
    }

    // =====================  UPDATE  =====================

    protected override void Update(GameTime gameTime)
    {
        var kb = Keyboard.GetState();
        var mouse = Mouse.GetState();
        if (kb.IsKeyDown(Keys.Escape)) Exit();

        if (!_spawned)
        {
            _forward = Vector3.Forward;
            _world.Update(GraphicsDevice, _camPos);
            TrySpawn();
            _effect.View = Matrix.CreateLookAt(_camPos, _camPos + _forward, Vector3.Up);
            _solidEffect.View = _effect.View;
            base.Update(gameTime);
            return;
        }

        HandleFpsHotkey(kb);
        HandleLeafHotkey(kb);
        HandleAoHotkey(kb);
        UpdateCamera(kb, mouse, (float)gameTime.ElapsedGameTime.TotalSeconds);
        HandleBlockSelection(kb, mouse);
        HandleBlockInteraction(mouse);
        _particleSystem.Update((float)gameTime.ElapsedGameTime.TotalSeconds, _world);
        _prevMouse = mouse;

        _world.Update(GraphicsDevice, _camPos);
        _effect.View = Matrix.CreateLookAt(_camPos, _camPos + _forward, Vector3.Up);
        _solidEffect.View = _effect.View;
        base.Update(gameTime);
    }

    private void HandleFpsHotkey(KeyboardState kb)
    {
        bool fDown = kb.IsKeyDown(Keys.F);
        if (fDown && !_fWasDown)
        {
            _capIndex = (_capIndex + 1) % _fpsCaps.Length;
            ApplyFpsCap();
        }
        _fWasDown = fDown;
    }

    private void UpdateCamera(KeyboardState kb, MouseState mouse, float dt)
    {
        var center = new Point(Window.ClientBounds.Width / 2, Window.ClientBounds.Height / 2);
        if (IsActive)
        {
            _yaw   -= (mouse.X - center.X) * 0.003f;
            _pitch -= (mouse.Y - center.Y) * 0.003f;
            _pitch = MathHelper.Clamp(_pitch, -1.5f, 1.5f);
            Mouse.SetPosition(center.X, center.Y);
        }

        var rotation = Matrix.CreateFromYawPitchRoll(_yaw, _pitch, 0);
        _forward = Vector3.Transform(Vector3.Forward, rotation);
        Vector3 right = Vector3.Transform(Vector3.Right, rotation);

        // V toggles fly mode
        bool vDown = kb.IsKeyDown(Keys.V);
        if (vDown && !_vWasDown) _player.Flying = !_player.Flying;
        _vWasDown = vDown;

        // Walking moves along the ground plane; flying moves where you look
        Vector3 fwd = _forward, rgt = right;
        if (!_player.Flying)
        {
            fwd = new Vector3(_forward.X, 0, _forward.Z);
            if (fwd != Vector3.Zero) fwd.Normalize();
            rgt = new Vector3(right.X, 0, right.Z);
            if (rgt != Vector3.Zero) rgt.Normalize();
        }

        Vector3 wish = Vector3.Zero;
        if (kb.IsKeyDown(Keys.W)) wish += fwd;
        if (kb.IsKeyDown(Keys.S)) wish -= fwd;
        if (kb.IsKeyDown(Keys.D)) wish += rgt;
        if (kb.IsKeyDown(Keys.A)) wish -= rgt;

        float vertical = (kb.IsKeyDown(Keys.Space) ? 1f : 0f) - (kb.IsKeyDown(Keys.LeftShift) ? 1f : 0f);
        bool groundLoaded = _world.IsColumnLoaded(  (int)MathF.Floor(_player.Position.X),
                                                    (int)MathF.Floor(_player.Position.Z));
        if (_player.Flying || groundLoaded)
            _player.Update(_world, dt, wish, kb.IsKeyDown(Keys.Space), vertical);

        // Respawn if you fall out of the world
        if (_player.Position.Y < -20) _player.Position = new Vector3(128.5f, 120f, 128.5f);

        _camPos = _player.Eye;
    }

    private void HandleBlockSelection(KeyboardState kb, MouseState mouse)
    {
        for (int i = 0; i < Math.Min(9, _hotbar.Length); i++)
            if (kb.IsKeyDown(Keys.D1 + i)) _slot = i;

        int scroll = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (scroll != 0)
            _slot = ((_slot - Math.Sign(scroll)) % _hotbar.Length + _hotbar.Length) % _hotbar.Length;
    }

    private void HandleBlockInteraction(MouseState mouse)
    {
        // Target the block under the crosshair using the configurable reach distance
        _hasTarget = _world.Raycast(_camPos, _forward, ReachDistance, out _target, out _targetPrev);
        if (!IsActive || !_hasTarget) return;

        bool leftClick  = mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        bool rightClick = mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;

        if (leftClick)
        {
            byte targetBlock = _world.GetBlock(_target.x, _target.y, _target.z);
            if (targetBlock != _airId)
            {
                _world.SetBlock(_target.x, _target.y, _target.z, _airId);
                _particleSystem.SpawnBurst(_world, _target.x, _target.y, _target.z, targetBlock);
            }
        }
        if (rightClick && _targetPrev != _target)
        {
            // Don't place a block inside the player's body
            if (!_player.Overlaps(_targetPrev.x, _targetPrev.y, _targetPrev.z))
                _world.SetBlock(_targetPrev.x, _targetPrev.y, _targetPrev.z, _hotbar[_slot]);
        }
    }

    // =====================  DRAW  =====================

    protected override void Draw(GameTime gameTime)
    {
        var frustum = new BoundingFrustum(_effect.View * _effect.Projection);
        _world.UpdateVisibility(frustum, _camPos);
        GraphicsDevice.Clear(Color.CornflowerBlue);
        GraphicsDevice.SamplerStates[0] = SamplerState.PointClamp;

        DrawWorld(frustum);
        DrawHud();
        UpdateTitle(gameTime);

        base.Draw(gameTime);
    }

    private void DrawWorld(BoundingFrustum frustum)
    {
        // Pass 1: opaque blocks
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
        foreach (var pass in _solidEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _world.DrawSolid(GraphicsDevice);
        }
        if (_hasTarget) DrawTargetOutline();

        // Draw particles with frustum culling applied (Single clean draw call)
        var rotation = Matrix.CreateFromYawPitchRoll(_yaw, _pitch, 0);
        Vector3 camRight = Vector3.Transform(Vector3.Right, rotation);
        Vector3 camUp = Vector3.Transform(Vector3.Up, rotation);
        
        _particleSystem.Draw(GraphicsDevice, _effect, frustum, camRight, camUp);

        // Pass 2: water, blended over the top...
        GraphicsDevice.BlendState = BlendState.AlphaBlend;
        GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
        GraphicsDevice.RasterizerState = RasterizerState.CullNone;
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _world.DrawWater(GraphicsDevice);
        }
    }

    private void DrawHud()
    {
        // PointClamp keeps the pixel art sharp when the tile is scaled up
        _sprites.Begin(samplerState: SamplerState.PointClamp);

        // Crosshair
        int cx = GraphicsDevice.Viewport.Width / 2, cy = GraphicsDevice.Viewport.Height / 2;
        _sprites.Draw(_pixel, new Rectangle(cx - 8, cy - 1, 16, 2), Color.White);
        _sprites.Draw(_pixel, new Rectangle(cx - 1, cy - 8, 2, 16), Color.White);

        // Selected block preview (bottom-left)
        const int size = 64, pad = 16, border = 4;
        var region = BlockRegistry.Instance.GetTextureRegion(_hotbar[_slot], 0);
        var src = region.PixelBounds;
        var dest = new Rectangle(pad, GraphicsDevice.Viewport.Height - pad - size, size, size);

        _sprites.Draw(_pixel, new Rectangle(dest.X - border, dest.Y - border, size + border * 2, size + border * 2), Color.Black * 0.6f);
        _sprites.Draw(_atlas, dest, src, BlockRegistry.Instance.GetTint(_hotbar[_slot]));

        _sprites.End();
    }

    private void DrawTargetOutline()
    {
        var min = new Vector3(_target.x, _target.y, _target.z) - new Vector3(0.002f);
        var max = min + new Vector3(1.004f);

        // Corner i: bit 0 = x, bit 1 = y, bit 2 = z
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++)
            p[i] = new Vector3((i & 1) == 0 ? min.X : max.X,
                               (i & 2) == 0 ? min.Y : max.Y,
                               (i & 4) == 0 ? min.Z : max.Z);

        int[] edges = { 0,1, 2,3, 4,5, 6,7,   0,2, 1,3, 4,6, 5,7,   0,4, 1,5, 2,6, 3,7 };
        var v = new VertexPositionColor[24];
        for (int i = 0; i < 24; i++) v[i] = new VertexPositionColor(p[edges[i]], Color.Black);

        _lineEffect.View = _effect.View;
        _lineEffect.Projection = _effect.Projection;
        foreach (var pass in _lineEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, v, 0, 12);
        }
    }

    // =====================  FPS / TITLE  =====================

    private void UpdateTitle(GameTime gameTime)
    {
        _frames++;
        if (gameTime.TotalGameTime.TotalSeconds - _lastFpsTime < 1.0) return;

        int cap = _fpsCaps[_capIndex];
        Window.Title = $"CuboidFever | {_frames} FPS (cap: {(cap == 0 ? "none" : cap)}) | " +
               $"chunks {_world.VisibleChunks}/{_world.TotalChunks} | queue {_world.PendingWork} | holding: {_hotbarNames[_slot]} | leaves {ChunkMesher.LeafCullDepth} | AO {(ChunkMesher.AmbientOcclusion ? "on" : "off")}";
        _frames = 0;
        _lastFpsTime = gameTime.TotalGameTime.TotalSeconds;
    }

    private void ApplyFpsCap()
    {
        int cap = _fpsCaps[_capIndex];
        if (cap == 0)
        {
            IsFixedTimeStep = false;
        }
        else
        {
            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromSeconds(1.0 / cap);
        }
    }

    private void HandleLeafHotkey(KeyboardState kb)
    {
        bool down = kb.IsKeyDown(Keys.L);
        if (down && !_lWasDown)
        {
            ChunkMesher.LeafCullDepth = (ChunkMesher.LeafCullDepth + 1) % (ChunkMesher.MaxLeafDepth + 1);
            _world.RemeshAll();
        }
        _lWasDown = down;
    }

    private void HandleAoHotkey(KeyboardState kb)
    {
        bool down = kb.IsKeyDown(Keys.O);
        if (down && !_oWasDown)
        {
            ChunkMesher.AmbientOcclusion = !ChunkMesher.AmbientOcclusion;
            _world.RemeshAll();
        }
        _oWasDown = down;
    }
}