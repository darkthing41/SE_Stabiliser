﻿#region Header
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Game.ModAPI.Ingame;
using VRageMath;

namespace SpaceEngineersScripting
{
	public class Wrapper
	{
		static void Main()
		{
			new CodeEditorEmulator().Main ("");
		}
	}


	class CodeEditorEmulator : Sandbox.ModAPI.Ingame.MyGridProgram
	{
		#endregion
		#region CodeEditor
		//Configuration
		//--------------------
		const string
			nameController = "Remote Control",
			nameGyro = "Gyroscope Override";

		const int
			period = 10; //if called at full rate, ( 1 => 60Hz, 60 => 1 Hz )

		static readonly MyBlockOrientation
			shipOrientation = new MyBlockOrientation
			        (Base6Directions.Direction.Forward, Base6Directions.Direction.Up);


		//Definitions
		//--------------------

		//Opcodes for use as arguments
		//-commands may be issued directly
		const string
			command_Initialise = "Init";   //args: <none>

		//Utility definitions
		static double AngleBetween (Vector3D a, Vector3D b){
			return Math.Acos( Vector3D.Dot(a, b) / (a.Length() * b.Length()) );
			//MyMath.AngleBetween(a,b)
		}

		static double NormaliseRadians (double angle){
			//Limit to within range of
			//-pi <= angle < pi
			//(assuming no more than one cycle out)
			if (angle >= Math.PI)
				return angle -(Math.PI * 2);
			else if (angle < -Math.PI)
				return angle +(Math.PI * 2);
			else
				return angle;
		}

		static readonly Base6Directions.Direction //Unit direction vectors for ship
			shipLeft = shipOrientation.TransformDirection(Base6Directions.Direction.Left),
			shipForward = shipOrientation.TransformDirection(Base6Directions.Direction.Forward),
			shipBackward = shipOrientation.TransformDirection(Base6Directions.Direction.Backward),
			shipUp = shipOrientation.TransformDirection(Base6Directions.Direction.Up);

		static Vector3D GridToWorld(Vector3I gridVector, IMyCubeGrid grid){
			//convert a grid vector for the specified grid into a world direction vector
			return Vector3D.Subtract (
				grid.GridIntegerToWorld (gridVector),
				grid.GridIntegerToWorld (Vector3I.Zero) );
		}

		//API definitions
		public struct Gyro
		{	//IMyGyro API wrapper
			//PROPERTIES:
			public static void SetPower (IMyGyro gyro, float power) {
				gyro.SetValue<float> ("Power", power);
			}
			public static void SetOverride(IMyGyro gyro, bool value){
				gyro.SetValue<bool>("Override", value);
			}
			public static void SetYaw(IMyGyro gyro, float yaw){
				gyro.SetValue<float>("Yaw", yaw);
			}
			public static void SetPitch(IMyGyro gyro, float pitch){
				gyro.SetValue<float>("Pitch", pitch);
			}
			public static void SetRoll(IMyGyro gyro, float roll){
				gyro.SetValue<float>("Roll", roll);
			}
			//Positive pitch => Nose up => normal Left
			//Positive yaw => Nose Right => normal Up
			//	*terminal displays yaw inverted from the underlying value
			//Positive roll => Right hand down => normal Backward
			//	*terminal displays roll inverted from the underlying value
		}


		//Internal Types
		//--------------------
		public struct PID
		{
			private float
				target;
			private float
				kP, kI, kD;

			private float
				errorLast,
				errorTotal;

			public void Reset(){
				errorLast = 0;
				errorTotal = 0;
			}

			public float Update(float measurement){

				float error = (target -measurement);
				errorTotal += error;

				float result =
					(kP * error)
					+(kI * errorTotal)
					+(kD * (error -errorLast));

				errorLast = error;

				return result;
			}

			public PID(float kP, float kI, float kD){
				target = 0;
				this.kP = kP;
				this.kI = kI;
				this.kD = kD;

				errorLast = 0;
				errorTotal = 0;
			}

			public float Target
			{
				get { return target; }
				set { target = value; Reset (); }
			}

			//configuration constants
			private const char delimiter = '\t';

			public string Store(){
				return
					target.ToString()
					+delimiter +kP.ToString()
					+delimiter +kI.ToString()
					+delimiter +kD.ToString()
					+delimiter +errorLast.ToString()
					+delimiter +errorTotal.ToString();
			}

			public bool TryRestore(string storage)
			{
				string[] elements = storage.Split(delimiter);
				return
					(elements.Length == 6)
					&& float.TryParse (elements[0], out target)
					&& float.TryParse (elements[1], out kP)
					&& float.TryParse (elements[2], out kI)
					&& float.TryParse (elements[3], out kD)
					&& float.TryParse (elements[4], out errorLast)
					&& float.TryParse (elements[5], out errorTotal);
			}
		}

		public struct Status
		{
			//program data not persistent across restarts
			public bool
				initialised;

			//status data persistent across restarts
			public int
				count;

			public PID
				controllerPitch,
				controllerRoll;

			//configuration constants
			private const char delimiter = ';';

			//Operations

			public void Initialise(){   //data setup
				count = 0;
				//adjust integral and derivative for time between calls
				controllerPitch = new PID(0.5f, 0.0005f*period, 5.0f/period);
				controllerRoll = new PID(0.5f, 0.0005f*period, 5.0f/period);
			}

			public string Store()
			{
				return
					count.ToString()
					+delimiter +controllerPitch.Store()
					+delimiter +controllerRoll.Store();
			}

			public bool TryRestore(string storage)
			{
				string[] elements = storage.Split(delimiter);
				return
					(elements.Length == 3)
					&& int.TryParse( elements[0], out count )
					&& controllerPitch.TryRestore( elements[1] )
					&& controllerRoll.TryRestore( elements[2] );
			}
		}


		//Global variables
		//--------------------
		bool restarted = true;
		Status status;

		IMyShipController controller;
		IMyGyro gyro;

		bool
			inverted;
		double
			pitch, roll;
		float
			correctionPitch, correctionRoll;


		//Program
		//--------------------
		public void Main(string argument)
		{
			//First ensure the system is able to process commands
			//-if necessary, perform first time setup
			//-if necessary or requested, initialise the system
			//  >otherwise, check that the setup is still valid
			if (restarted) {
				//script has been reloaded
				//-may be first time running
				//-world may have been reloaded (or script recompiled)
				if (Storage == null) {
					//use default values
					status.Initialise();
				} else {
					//attempt to restore saved values
					//  -otherwise use defaults
					Echo ("restoring saved state...");
					if ( !status.TryRestore(Storage) ){
						Echo ("restoration failed.");
						status.Initialise();
					}
				}
				status.initialised = false; //we are not initialised after restart
				Storage = null;	//will be resaved iff initialisation is successful
				restarted = false;
			}
			if ( !status.initialised || argument == command_Initialise) {
				//if we cannot initialise, end here
				if ( !Initialise() )
					return;
			}
			else if ( !Validate() ) {
				//if saved state is not valid, try re-initialising
				//if we cannot initialise, end here
				if ( !Initialise() )
					return;
			}

			if (argument == command_Initialise) {
				Echo ("resetting.");
				status.Initialise ();
			}

			//Perform main processing
			if (++status.count == period) {
				status.count = 0;

				//Perform main processing
				Update ();

				//var temp = ((IMyTextPanel)GridTerminalSystem.GetBlockWithName ("Display"));
				//if (temp != null) {
				//	temp.WritePublicText ( String.Format(
				//		"p: {0,-08:F}\nr: {1,-08:F}\n\ncp: {2,-08:F}\ncr: {3,-08:F}",
				//		pitch*180 / Math.PI,
				//		roll*180 / Math.PI,
				//		correctionPitch,
				//		correctionRoll
				//	));
				//}
			}

			//Echo system status
			Echo ("inv: " +inverted.ToString() );
			Echo ("pitch: " +(pitch*180 / Math.PI).ToString() );
			Echo ("roll: " +(roll*180 / Math.PI).ToString() );

			//Save current status
			Storage = status.Store();
			Echo (Storage);
		}


		private void Update()
		{
			//Find deviation from level flight

			//-Prepare data
			//  >find local gravity vector (to define "level")
			//  >transform our cardinal directions into the same vector space as gravity
			Vector3D
				worldGravity = controller.GetNaturalGravity ();

			Vector3D
				worldForward = GridToWorld (Base6Directions.GetIntVector(shipForward), controller.CubeGrid),
				worldLeft = GridToWorld (Base6Directions.GetIntVector(shipLeft), controller.CubeGrid),
				worldUp = GridToWorld (Base6Directions.GetIntVector(shipUp), controller.CubeGrid);

			//-Calculate angles between gravity and our axes
			//  >calculate basic angle between gravity and axes
			//	>rotate so 0 indicates level flight
			//  >determine inversion in lieu of a reference direction
			//  >expand angles to 360-degree range from 180-degree by checking inversion

			//If "up" is in the same direction as gravity, we are inverted.
			inverted = Vector3D.Dot(worldGravity, worldUp) > 0;

			//Angle between up-right plane and the gravity ("ground") plane
			//-independent of yaw
			//-independent of roll
			//-corresponds to pitch
			//Calculate pitch as angle between these normals
			pitch = AngleBetween(worldGravity, worldForward) -Math.PI/2;
			if (inverted)
				pitch = Math.PI -pitch;
			pitch = NormaliseRadians(pitch);

			//Angle between up-forward plane and the gravity ("ground") plane
			//-independent of yaw
			//-independent of pitch
			//-corresponds to roll
			//Calculate pitch as angle between these normals
			roll = AngleBetween(worldGravity, worldLeft) -Math.PI/2;
			if (inverted)
				roll = Math.PI -roll;
			roll = NormaliseRadians(roll);

			//Update gyros to force stability
			correctionPitch = status.controllerPitch.Update( (float)pitch );
			correctionRoll = status.controllerRoll.Update( (float)roll );

			float
				cPitch = correctionPitch *10,
				cRoll = correctionRoll *10;

			SetUpGyro( gyro, cPitch, cRoll, 0.0f );
		}


		private void SetUpGyro(IMyGyro gyro, float pitch, float roll, float yaw){
			//Set up a gyro, accounting for gyro orientation

			//Positive pitch => Nose up => normal Left
			SetGyroAxis(gyro, shipLeft, pitch);

			//Positive roll => Right hand down => normal Backward
			SetGyroAxis(gyro, shipBackward, roll);

			//Positive yaw => Nose Right => normal Up
			SetGyroAxis(gyro, shipUp, yaw);
		}

		private void SetGyroAxis(IMyGyro gyro, Base6Directions.Direction planeNormal, float value)
		{
			//-see which axis this originally came from (reverse Orientation)
			//-set up gyro accordingly (to which plane is this normal to?)
			switch (gyro.Orientation.TransformDirectionInverse (planeNormal)) {
				case Base6Directions.Direction.Left:
					//unchanged
					Gyro.SetPitch(gyro, value);
					break;
				case Base6Directions.Direction.Right:
					Gyro.SetPitch(gyro, -value);
					break;
				case Base6Directions.Direction.Up:
					Gyro.SetYaw(gyro, value);
					break;
				case Base6Directions.Direction.Down:
					Gyro.SetYaw(gyro, -value);
					break;
				case Base6Directions.Direction.Forward:
					Gyro.SetRoll (gyro, -value);
					break;
				case Base6Directions.Direction.Backward:
					Gyro.SetRoll (gyro, value);
					break;
				default:
					Echo ("ERROR: unknown Direction returned");
					return;
			};
		}


		private bool FindBlock<BlockType>(out BlockType block, string nameBlock, ref List<IMyTerminalBlock> temp)
				where BlockType : class, IMyTerminalBlock
		{
			block = null;
			GridTerminalSystem.GetBlocksOfType<BlockType> (temp);
			for (int i=0; i<temp.Count; i++){
				if (temp[i].CustomName == nameBlock) {
					if (block == null) {
						block = (BlockType)temp[i];
					} else {
						Echo ("ERROR: duplicate name \"" +nameBlock +"\"");
						return false;
					}
				}
			}
			//verify that the block was found
			if (block == null) {
				Echo ("ERROR: block not found \"" +nameBlock +"\"");
				return false;
			}
			return true;
		}

		private bool Initialise()
		{
			status.initialised = false;
			Echo ("initialising...");

			var temp = new List<IMyTerminalBlock>();

			//Find Controller and verify that it is operable
			if ( !( FindBlock<IMyShipController>(out controller, nameController, ref temp)
			        && ValidateBlock(controller, callbackRequired:false) ))
				return false;

			//Find Gyro and verify that it is operable
			if ( !( FindBlock<IMyGyro>(out gyro, nameGyro, ref temp)
			        && ValidateBlock(gyro, callbackRequired:false) ))
				return false;

			status.initialised = true;
			Echo ("Initialisation completed with no errors.");
			return true;
		}


		private bool ValidateBlock(IMyTerminalBlock block, bool callbackRequired=true)
		{
			//check for block deletion?

			//check that we have required permissions to control the block
			if ( ! Me.HasPlayerAccess(block.OwnerId) ) {
				Echo ("ERROR: no permissions for \"" +block.CustomName +"\"");
				return false;
			}

			//check that the block has required permissions to make callbacks
			if ( callbackRequired && !block.HasPlayerAccess(Me.OwnerId) ) {
				Echo ("ERROR: no permissions on \"" +block.CustomName +"\"");
				return false;
			}

			//check that block is functional
			if (!block.IsFunctional) {
				Echo ("ERROR: non-functional block \"" +block.CustomName +"\"");
				return false;
			}

			return true;
		}


		private bool Validate(){
			bool valid =
				ValidateBlock (controller, callbackRequired:false) &
				ValidateBlock (gyro, callbackRequired:false);

			if ( !valid ) {
				Echo ("Validation of saved blocks failed.");
			}
			return valid;
		}
		#endregion
		#region footer
	}
}
#endregion