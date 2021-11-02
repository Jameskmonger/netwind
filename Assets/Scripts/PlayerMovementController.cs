﻿using UnityEngine;
using Unity.Netcode;
using System;

public class PlayerMovementController : NetworkBehaviour
{
    private static readonly int HISTORY_LENGTH = 64;
    private static readonly int DISPLAY_OFFSET = 2;

    [Serializable]
    private struct CharacterState
    {
        public Vector3 position;

        public bool Equals(CharacterState other)
        {
            return Vector3.Distance(this.position, other.position) < 0.05f;
        }
    }

    [Header("Configuration")]
    [SerializeField] private float moveSpeed = 2f;

    [Header("Dependencies")]
    [SerializeField] private InputProvider inputProvider;

    [Header("Runtime")]
    [SerializeField] private TickHistoryBuffer<InputProvider.State> inputCache;
    [SerializeField] private TickHistoryBuffer<CharacterState> stateCache;

    [SerializeField] private int earliestInput;
    [SerializeField] private int earliestState;
    [SerializeField] private bool needsResimulation = false;

    [SerializeField] private CharacterState fromState;
    [SerializeField] private CharacterState toState;
    [SerializeField] private double fromTime;
    [SerializeField] private double toTime;

    private void OnEnable()
    {
        inputProvider ??= GetComponent<InputProvider>();

        NetworkManager.Singleton.NetworkTickSystem.Tick += NetworkUpdate;

        fromState = CaptureState();
        toState = CaptureState();
    }

    private void OnDisable()
    {
        if (NetworkManager)
            NetworkManager.NetworkTickSystem.Tick -= NetworkUpdate;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        int currentTick = NetworkManager.LocalTime.Tick;
        earliestInput = currentTick;
        earliestState = currentTick;

        inputCache = new TickHistoryBuffer<InputProvider.State>(HISTORY_LENGTH, currentTick);
        stateCache = new TickHistoryBuffer<CharacterState>(HISTORY_LENGTH, currentTick);

        stateCache.Set(CaptureState(), currentTick);
        stateCache.defaultValue = CaptureState();
    }

    private void Update()
    {
        var currentTime = NetworkManager.LocalTime.Time;
        var f = 1.0 - (toTime - currentTime) / (toTime - fromTime);

        var interpolatedState = new CharacterState()
        {
            position = Vector3.Lerp(fromState.position, toState.position, (float)f)
        };

        ApplyState(interpolatedState);
    }

    private void NetworkUpdate()
    {
        float deltaTime = NetworkManager.LocalTime.FixedDeltaTime;

        int currentTick = NetworkManager.LocalTime.Tick;

        fromState = CaptureState();
        fromTime = NetworkManager.LocalTime.Time;
        toTime = fromTime + deltaTime;

        if (!IsLocalPlayer && IsServer)
        {
            if (needsResimulation)
            {
                ResimulateFrom(earliestInput, currentTick + 1, deltaTime);

                needsResimulation = false;
                earliestInput = currentTick;
            }
        }

        if (IsLocalPlayer && !IsServer)
        {
            inputCache.Set(inputProvider.Current, currentTick);
            CommitInputServerRpc(inputCache.Get(currentTick), currentTick);

            if (needsResimulation)
            {
                ResimulateFrom(earliestState, currentTick + 1, deltaTime);

                needsResimulation = false;
                earliestState = 0;
            }
        }

        if (IsLocalPlayer && IsServer)
        {
            inputCache.Set(inputProvider.Current, currentTick);
            ResimulateFrom(currentTick, currentTick + 1, deltaTime);
        }

        toState = stateCache.Get(currentTick - DISPLAY_OFFSET);
    }

    private void ResimulateFrom(int from, int to, float deltaTime)
    {
        for (int tick = from; tick <= to; ++tick)
        {
            stateCache.Set(Simulate(inputCache.Get(tick), stateCache.Get(tick - 1), deltaTime), tick);

            if (IsServer)
                CommitStateClientRpc(stateCache.Get(tick), tick);
        }
    }

    private CharacterState Simulate(InputProvider.State input, CharacterState sourceState, float deltaTime)
    {
        ApplyState(sourceState);

        transform.position += input.movement * moveSpeed * deltaTime;

        return CaptureState();
    }

    private void ApplyState(CharacterState state)
    {
        transform.position = state.position;
    }

    private CharacterState CaptureState()
    {
        return new CharacterState()
        {
            position = transform.position
        };
    }

    [ClientRpc]
    private void CommitStateClientRpc(CharacterState state, int tick)
    {
        if (stateCache.Get(tick).Equals(state))
            return;

        stateCache.Set(state, tick);

        earliestState = Math.Max(earliestState, tick);
        needsResimulation = true;
    }

    [ServerRpc]
    private void CommitInputServerRpc(InputProvider.State input, int tick)
    {
        inputCache.Set(input, tick);

        earliestInput = Math.Min(earliestInput, tick);
        needsResimulation = true;
    }

    private void OnDrawGizmosSelected()
    {
        var currentTime = NetworkManager.LocalTime.Time;
        var f = 1.0 - (toTime - currentTime) / (toTime - fromTime);

        var interpolatedState = new CharacterState()
        {
            position = Vector3.Lerp(fromState.position, toState.position, (float)f)
        };

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(fromState.position, 1f);

        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(toState.position, 1f);

        Gizmos.color = Color.Lerp(Color.red, Color.green, (float)f);
        Gizmos.DrawWireSphere(interpolatedState.position, 0.5f);
    }
}
