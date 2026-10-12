using UnityEngine;

namespace Game.Weather
{
    /// <summary>
    /// 测试玩家（仅用于验证天气系统，正式项目可删除）：
    /// - WASD / 方向键移动，相机自动跟随；
    /// - 运行时生成占位小人贴图，使用 URP 2D Sprite-Lit 材质响应 2D 全局光与天气色调；
    /// - 带 Rigidbody2D + Player 标签，供 GroundWeatherFX 的脚印功能使用。
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public sealed class TestPlayer : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 4.5f;

        private Rigidbody2D body;

        private void Reset() => EnsureVisuals();

        private void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            body.gravityScale = 0f;
            body.freezeRotation = true;
            EnsureVisuals();
        }

        private void Update()
        {
            if (body == null) return;
            float x = Input.GetAxisRaw("Horizontal");
            float y = Input.GetAxisRaw("Vertical");
            Vector2 move = new Vector2(x, y);
            body.velocity = move.sqrMagnitude > 1f ? move.normalized * moveSpeed : move * moveSpeed;
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 position = transform.position;
            cam.transform.position = new Vector3(position.x, position.y, cam.transform.position.z);
        }

        private void EnsureVisuals()
        {
            SpriteRenderer sr = GetComponent<SpriteRenderer>();
            if (sr == null) sr = gameObject.AddComponent<SpriteRenderer>();
            if (sr.sprite == null)
            {
                Texture2D texture = CreateBodyTexture(48);
                sr.sprite = Sprite.Create(texture, new Rect(0f, 0f, 48, 48), new Vector2(0.5f, 0.2f), 48f);
            }
            // 使用 URP 2D 受光材质，全局光/天气色调才能作用到角色身上。
            if (sr.sharedMaterial == null || sr.sharedMaterial.shader == null ||
                sr.sharedMaterial.shader.name == "Sprites/Default")
            {
                Shader lit = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
                if (lit != null) sr.sharedMaterial = new Material(lit) { name = "TestPlayer Lit" };
            }
        }

        /// <summary>占位小人：软边胶囊形，暖沙色。</summary>
        private static Texture2D CreateBodyTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center.x) / (size * 0.5f);
                    float dy = (y - center.y) / (size * 0.5f);
                    // 胶囊：横向窄、纵向拉长，顶部略收
                    float halfWidth = dy > 0.35f ? 0.34f - (dy - 0.35f) * 0.55f : 0.34f;
                    float edge = Mathf.Clamp01((halfWidth - Mathf.Abs(dx)) / 0.12f);
                    float bodyMask = dy > -1f && dy < 0.95f ? 1f : 0f;
                    float alpha = Mathf.Clamp01(edge * bodyMask);
                    texture.SetPixel(x, y, new Color(0.90f, 0.74f, 0.52f, alpha));
                }
            }
            texture.Apply();
            return texture;
        }
    }
}