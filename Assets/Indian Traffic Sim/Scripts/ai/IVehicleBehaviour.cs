// ─────────────────────────────────────────────────────────────────────────────
// IVehicleBehaviour — contract that every vehicle AI module must implement.
//
// OOP role: Interface + Polymorphism.
//   TrafficVehicle holds a List<IVehicleBehaviour> and calls Tick/OnSpawn/OnDespawn
//   on all of them uniformly — it does not know or care which concrete class each
//   module is. Adding a new behaviour (horn, overtake, time-of-day awareness) means
//   implementing this interface and registering the instance. Nothing else changes.
// ─────────────────────────────────────────────────────────────────────────────

public interface IVehicleBehaviour
{
    /// <summary>Called every FixedUpdate. dt = Time.fixedDeltaTime.</summary>
    void Tick(float dt);

    /// <summary>Called once after Initialize() and AssignLane() complete.</summary>
    void OnSpawn();

    /// <summary>Called when the vehicle is about to be destroyed.</summary>
    void OnDespawn();
}