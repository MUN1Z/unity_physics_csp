﻿using UnityEngine;
using System.Collections.Generic;

public class Logic : MonoBehaviour
{
    private struct Inputs
    {
        public bool up;
        public bool down;
        public bool left;
        public bool right;
        public bool jump;
    }

    private struct InputMessage
    {
        public float delivery_time;
        public uint start_tick_number;
        public List<Inputs> inputs;
    }

    private struct ClientState
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    private struct StateMessage
    {
        public float delivery_time;
        public uint tick_number;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
        public Vector3 angular_velocity;
    }

    public Transform local_player_camera_transform;
    public float player_movement_impulse;
    public float player_jump_y_threshold;
    public GameObject client_player;
    public GameObject server_player;
    public GameObject server_display_player;
    public GameObject proxy_player;
    
    public const float c_latency = 0.1f; // todo(jbr) make a variable to tweak
    public const float c_packet_loss = 0.9f;

    private const float c_max_prediction_error_pos = 0.1f;
    private const float c_max_prediction_error_pos_sq = c_max_prediction_error_pos * c_max_prediction_error_pos;
    private const float c_max_prediction_error_rot_dot = 0;//0.996f; // ~5 degrees

    private float client_timer;
    private uint client_tick_number;
    private uint client_last_received_state_tick;
    private const int c_client_buffer_size = 1024;
    private ClientState[] client_state_buffer; // client stores predicted moves here
    private Inputs[] client_input_buffer; // client stores predicted inputs here
    private Queue<StateMessage> client_state_msgs;

    public uint server_snapshot_rate;
    private uint server_tick_number;
    private uint server_tick_accumulator;
    private Queue<InputMessage> server_input_msgs;

    private void Start()
    {
        this.client_timer = 0.0f;
        this.client_tick_number = 0;
        this.client_last_received_state_tick = 0;
        this.client_state_buffer = new ClientState[c_client_buffer_size];
        this.client_input_buffer = new Inputs[c_client_buffer_size];
        this.client_state_msgs = new Queue<StateMessage>();

        this.server_tick_accumulator = 0;
        this.server_input_msgs = new Queue<InputMessage>();
    }

    private void Update()
    {
        // client update

        // enable client player, disable server player
        this.server_player.SetActive(false);
        this.client_player.SetActive(true);

        float dt = Time.fixedDeltaTime;
        float client_timer = this.client_timer;
        uint client_tick_number = this.client_tick_number;

        client_timer += Time.deltaTime;
        while (client_timer >= dt)
        {
            client_timer -= dt;

            uint buffer_slot = client_tick_number % c_client_buffer_size;

            // sample and store inputs for this tick
            Inputs inputs;
            inputs.up = Input.GetKey(KeyCode.W);
            inputs.down = Input.GetKey(KeyCode.S);
            inputs.left = Input.GetKey(KeyCode.A);
            inputs.right = Input.GetKey(KeyCode.D);
            inputs.jump = Input.GetKey(KeyCode.Space);
            this.client_input_buffer[buffer_slot] = inputs;

            // store state for this tick, then use current state + input to step simulation
            this.ClientStoreCurrentStateAndStep(
                ref this.client_state_buffer[buffer_slot], 
                this.client_player.GetComponent<Rigidbody>(), 
                inputs, 
                dt);
            
            // send input packet to server
            if (Random.value < Logic.c_packet_loss)
            {
                InputMessage input_msg;
                input_msg.delivery_time = Time.time + Logic.c_latency;
                input_msg.start_tick_number = this.client_last_received_state_tick;
                input_msg.inputs = new List<Inputs>();
                for (uint tick = this.client_last_received_state_tick; tick <= client_tick_number; ++tick)
                {
                    input_msg.inputs.Add(this.client_input_buffer[tick % c_client_buffer_size]);
                }
                this.server_input_msgs.Enqueue(input_msg);
            }

            ++client_tick_number;
        }

        if (this.ClientHasStateMessage())
        {
            StateMessage state_msg = this.client_state_msgs.Dequeue();
            while (this.ClientHasStateMessage()) // make sure if there are any newer state messages available, we use those instead
            {
                state_msg = this.client_state_msgs.Dequeue();
            }

            this.client_last_received_state_tick = state_msg.tick_number;

            this.proxy_player.transform.position = state_msg.position;
            this.proxy_player.transform.rotation = state_msg.rotation;

            // if there's enough error between client&server, then rewind&replay
            uint buffer_slot = state_msg.tick_number % c_client_buffer_size;
            Vector3 position_error = state_msg.position - this.client_state_buffer[buffer_slot].position;
            float rotation_error = Quaternion.Dot(state_msg.rotation, this.client_state_buffer[buffer_slot].rotation);
            if (position_error.sqrMagnitude > c_max_prediction_error_pos_sq ||
                rotation_error < c_max_prediction_error_rot_dot)
            {
                Rigidbody rigidbody = this.client_player.GetComponent<Rigidbody>();
                rigidbody.position = state_msg.position;
                rigidbody.rotation = state_msg.rotation;
                rigidbody.velocity = state_msg.velocity;
                rigidbody.angularVelocity = state_msg.angular_velocity;

                uint rewind_tick_number = state_msg.tick_number;
                while (rewind_tick_number < client_tick_number)
                {
                    buffer_slot = rewind_tick_number % c_client_buffer_size;
                    this.ClientStoreCurrentStateAndStep(ref this.client_state_buffer[buffer_slot], rigidbody, this.client_input_buffer[buffer_slot], dt);

                    ++rewind_tick_number;
                }
            }
        }

        this.client_timer = client_timer;
        this.client_tick_number = client_tick_number;

        // server update

        // enable server player, disable client player
        this.client_player.SetActive(false);
        this.server_player.SetActive(true);

        uint server_tick_number = this.server_tick_number;
        uint server_tick_accumulator = this.server_tick_accumulator;
        Rigidbody server_rigidbody = this.server_player.GetComponent<Rigidbody>();
        while (this.server_input_msgs.Count > 0 && Time.time >= this.server_input_msgs.Peek().delivery_time)
        {
            InputMessage input_msg = this.server_input_msgs.Dequeue();

            // message contains an array of inputs, calculate what tick the final one is
            uint max_tick = input_msg.start_tick_number + (uint)input_msg.inputs.Count - 1;

            // if that tick is greater than or equal to the current tick we're on, then it
            // has inputs which are new
            if (max_tick >= server_tick_number)
            {
                // there may be some inputs in the array that we've already had,
                // so figure out where to start
                int start_i = (int)(server_tick_number - input_msg.start_tick_number);

                // run through all relevant inputs, and step player forward
                for (int i = start_i; i < input_msg.inputs.Count; ++i)
                {
                    this.PrePhysicsStep(server_rigidbody, input_msg.inputs[i]);
                    Physics.Simulate(dt);

                    ++server_tick_accumulator;
                }
                
                this.server_display_player.transform.position = server_rigidbody.position;
                this.server_display_player.transform.rotation = server_rigidbody.rotation;

                server_tick_number = max_tick + 1;
            }
        }
        if (server_tick_accumulator >= this.server_snapshot_rate)
        {
            server_tick_accumulator = 0;

            if (Random.value < Logic.c_packet_loss)
            {
                StateMessage state_msg;
                state_msg.delivery_time = Time.time + Logic.c_latency;
                state_msg.tick_number = server_tick_number;
                state_msg.position = server_rigidbody.position;
                state_msg.rotation = server_rigidbody.rotation;
                state_msg.velocity = server_rigidbody.velocity;
                state_msg.angular_velocity = server_rigidbody.angularVelocity;
                this.client_state_msgs.Enqueue(state_msg);
            }
        }
        this.server_tick_number = server_tick_number;
        this.server_tick_accumulator = server_tick_accumulator;

        // finally, we're viewing the client, so enable the client player, disable server again
        this.server_player.SetActive(false);
        this.client_player.SetActive(true);
    }

    private void PrePhysicsStep(Rigidbody rigidbody, Inputs inputs)
    {
        if (this.local_player_camera_transform != null)
        {
            if (inputs.up)
            {
                rigidbody.AddForce(this.local_player_camera_transform.forward * this.player_movement_impulse, ForceMode.Impulse);
            }
            if (inputs.down)
            {
                rigidbody.AddForce(-this.local_player_camera_transform.forward * this.player_movement_impulse, ForceMode.Impulse);
            }
            if (inputs.left)
            {
                rigidbody.AddForce(-this.local_player_camera_transform.right * this.player_movement_impulse, ForceMode.Impulse);
            }
            if (inputs.right)
            {
                rigidbody.AddForce(this.local_player_camera_transform.right * this.player_movement_impulse, ForceMode.Impulse);
            }
            if (rigidbody.transform.position.y <= this.player_jump_y_threshold && inputs.jump)
            {
                rigidbody.AddForce(this.local_player_camera_transform.up * this.player_movement_impulse, ForceMode.Impulse);
            }
        }
    }

    private bool ClientHasStateMessage()
    {
        return this.client_state_msgs.Count > 0 && Time.time >= this.client_state_msgs.Peek().delivery_time;
    }

    private void ClientStoreCurrentStateAndStep(ref ClientState current_state, Rigidbody rigidbody, Inputs inputs, float dt)
    {
        current_state.position = rigidbody.position;
        current_state.rotation = rigidbody.rotation;

        this.PrePhysicsStep(rigidbody, inputs);
        Physics.Simulate(dt);
    }     
}
