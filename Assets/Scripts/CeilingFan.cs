using UnityEngine;

public class CeilingFan : MonoBehaviour
{
    [SerializeField] private GameObject ceilingFan;
    private float rotationSpeed = 100f; // degrees per second

    public void FixedUpdate()
    {
        ceilingFan.transform.Rotate(Vector3.up, rotationSpeed * Time.fixedDeltaTime);
    }
}

