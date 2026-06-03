import pickle
import json

# Define your input and output file names
input_filename = 'B4.json-4-paths_edge-disjoint-False.pkl'
output_filename = 'formatted_paths.json'

def convert_pickle_to_json():
    try:
        # 1. Load the original pickle data
        with open(input_filename, 'rb') as f:
            data = pickle.load(f)

        formatted_data = {}

        # 2. Iterate through the dictionary and format it
        for key, paths in data.items():
            # Format the key to "Source_Target" (e.g., "0_1")
            if isinstance(key, tuple):
                str_key = f"{key[0]}_{key[1]}"
            else:
                str_key = str(key)

            formatted_paths = []
            for path in paths:
                # Convert every node ID integer in the path to a string
                formatted_path = [str(node) for node in path]
                formatted_paths.append(formatted_path)

            formatted_data[str_key] = formatted_paths

        # 3. Save the neatly formatted data to a JSON file
        with open(output_filename, 'w') as f:
            json.dump(formatted_data, f, indent=4)

        print(f"Success! Your data has been converted and saved to '{output_filename}'.")

    except FileNotFoundError:
        print(f"Error: Could not find the file '{input_filename}'. Please ensure it is in the same folder as this script.")
    except Exception as e:
        print(f"An unexpected error occurred: {e}")

if __name__ == "__main__":
    convert_pickle_to_json()